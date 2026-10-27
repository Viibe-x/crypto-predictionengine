using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using DexScannerBot.Models;
using DexScannerBot.Services;

namespace DexScannerBot.Core
{
    public class ScanWorker
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public int WorkerId { get; }
        public string SourceFeed { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public int RequestsPerMin { get; set; }
        public double ErrorRateLast60s { get; set; }
        public int QueueDepth { get; internal set; }
        public int ScannedCount { get; private set; }

        private readonly DexScreenerClient _client;
        private readonly AnalysisEngine _engine;
        private readonly RugDetectService _rugDetector;
        private readonly SecurityIntelService _intelService;
        private readonly SpikePredictionEngine _spikeEngine;
        private readonly SpikeMonitor? _monitor;
        private readonly OutcomeTracker? _outcomes;
        private readonly WebSocketServer? _wsServer;
        private readonly AppConfig _config;
        private readonly CircuitBreaker _circuitBreaker;
        private readonly RateLimiter _rateLimiter;
        private readonly CancellationTokenSource _cts = new();
        private Task? _workerTask;
        private DateTime _windowStart;
        private DateTime _startTime;
        private int _errorsInWindow;
        private int _callsAtWindowStart;

        public event Action<ScanWorker, TokenAnalysis>? OnTokenSurfaced;
        public event Action<ScanWorker, RugDetectionResult>? OnRugAlert;

        public ScanWorker(int workerId, DexScreenerClient client, AnalysisEngine engine,
            RugDetectService rugDetector, SpikePredictionEngine spikeEngine,
            WebSocketServer? wsServer, AppConfig config,
            SecurityIntelService? intelService = null, SpikeMonitor? monitor = null,
            OutcomeTracker? outcomes = null)
        {
            WorkerId = workerId;
            _client = client;
            _engine = engine;
            _rugDetector = rugDetector;
            _intelService = intelService ?? new SecurityIntelService();
            _spikeEngine = spikeEngine;
            _monitor = monitor;
            _outcomes = outcomes;
            _wsServer = wsServer;
            _config = config;
            _circuitBreaker = new CircuitBreaker(threshold: 5, retryAfterSeconds: 30);
            _rateLimiter = new RateLimiter(maxTokens: 100, refillPeriodMs: 1000);
            _windowStart = DateTime.UtcNow;
            _startTime = DateTime.UtcNow;
        }

        public void Start()
        {
            _workerTask = Task.Run(WorkerLoopAsync, _cts.Token);
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            if (_workerTask != null) await _workerTask;
        }

        private async Task WorkerLoopAsync()
        {
            SourceFeed = $"Worker-{WorkerId}";
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!_rateLimiter.TryAcquire()) { await Task.Delay(50); continue; }
                    QueueDepth = _client.QueueCount;

                    var candidate = await RetryPolicy.ExecuteAsync(
                            () => _client.GetNextCandidateAsync(),
                            maxRetries: _config.Execution.RetryAttempts,
                            baseDelayMs: _config.Execution.RetryBackoffMs,
                            circuitBreaker: _circuitBreaker,
                            _cts.Token);

                    if (candidate.Pair == null)
                    {
                        await Task.Delay(_config.Scanner.PollIntervalMs);
                        continue;
                    }
                    var qp = candidate.Pair;
                    var qb = candidate.Boost;
                    UpdateErrorRate(true);

                    // Every snapshot feeds the live tape, even ones the gates reject.
                    _monitor?.Ingest(qp);

                    ScannedCount++;
                    string? rejection;
                    var analysis = _engine.Analyze(qp, qb, out rejection);

                    if (analysis == null)
                    {
                        continue;
                    }

                    // External, verified security intel (RugCheck/GoPlus), 10-min cached.
                    var intel = await _intelService.GetAsync(qp.ChainId ?? "", analysis.Address ?? "", _cts.Token);

                    // Hard fraud proof from a real security provider → unconditional block.
                    if (intel != null && intel.IsCriticalThreat)
                    {
                        Console.WriteLine($"[INTEL-BLOCK] {analysis.Symbol} — {string.Join(", ", intel.ProviderRisks)}");
                        _client.RecordSurfaced(analysis.Address);
                        continue;
                    }

                    var rugResult = _rugDetector.Analyze(qp, analysis, intel);

                    if (rugResult.RiskScore.ShouldBlock)
                    {
                        OnRugAlert?.Invoke(this, rugResult);
                        if (_wsServer != null)
                        {
                            _ = _wsServer.BroadcastAsync(JsonSerializer.Serialize(new
                            {
                                type = "rug_alert",
                                payload = new
                                {
                                    id = Guid.NewGuid(),
                                    tokenId = qp.BaseToken?.Address,
                                    symbol = rugResult.Symbol,
                                    riskScore = rugResult.RiskScore.TotalScore,
                                    riskTier = rugResult.RiskScore.RiskTier,
                                    vectors = new
                                    {
                                        contract = new { honeypot = rugResult.Contract.IsHoneypot, mintable = rugResult.Contract.IsMintable },
                                        liquidity = new { rugSignature = rugResult.Liquidity.HasRugPullSignature, lpConcentration = rugResult.Liquidity.Top10LpConcentration },
                                        developer = new { devHeavy = rugResult.Developer.IsDevHeavy, mixedFunding = rugResult.Developer.IsFromMixer, washTrading = rugResult.Developer.IsWashTrading },
                                        social = new { botCampaign = rugResult.Social.HasBotCampaign }
                                    },
                                    recommendedAction = rugResult.RecommendedAction,
                                    timestamp = DateTime.UtcNow
                                }
                            }, _jsonOptions));
                        }
                        continue;
                    }

                    if (rugResult.RiskScore.ShouldReduceSize)
                    {
                        analysis.WarningSignals.Add($"⚠️ Rug risk elevated ({rugResult.RiskScore.TotalScore}/100) — reducing position size by 50%");
                        analysis.TotalScore = Math.Max(40, analysis.TotalScore - 10);
                    }

                    PredictionStamp.StampRug(analysis, rugResult);
                    if (analysis.VerifiedReputable)
                        analysis.BullishSignals.Add($"🛡️ {rugResult.Verification}");
                    else if (rugResult.RiskScore.TotalScore >= 41)
                        analysis.WarningSignals.Add($"🛡️ {rugResult.Verification}");

                    // Spike prediction (live tape features when this token has history).
                    var live = _monitor?.GetLive(analysis.Chain, analysis.Address);
                    var prediction = _spikeEngine.Predict(analysis, intel, rugResult, live);
                    PredictionStamp.Apply(analysis, prediction);

                    if (prediction.IsScamBlocked)
                    {
                        Console.WriteLine($"[SCAM-BLOCK] {analysis.Symbol} — {prediction.ScamReason}");
                        _client.RecordSurfaced(analysis.Address);
                        continue;
                    }

                    // Every non-blocked call is followed so the model learns and calibrates.
                    try { _outcomes?.Record(analysis, prediction); }
                    catch (Exception ex) { Console.WriteLine($"[Worker-{WorkerId}] Outcome tracking error: {ex.Message}"); }

                    // Keep the token on the live tape so the spike can be timed precisely.
                    _monitor?.Track(analysis, intel, prediction);

                    // Mark this address in the shared dedup set BEFORE broadcasting
                    _client.RecordSurfaced(analysis.Address);

                    OnTokenSurfaced?.Invoke(this, analysis);

                    if (_wsServer != null)
                    {
                        _ = _wsServer.BroadcastAsync(JsonSerializer.Serialize(new
                        {
                            type = "token_surfaced",
                            payload = TokenSurfacedMessage.From(analysis, prediction, rugResult, intel)
                        }, _jsonOptions));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Worker-{WorkerId}] Error: {ex.Message}");
                    UpdateErrorRate(false);
                    await Task.Delay(200);
                }
            }
        }

        private int _iterationsInWindow;

        private void UpdateErrorRate(bool success)
        {
            var now = DateTime.UtcNow;
            if ((now - _windowStart).TotalSeconds > 60)
            {
                RequestsPerMin = _client.TotalApiCalls - _callsAtWindowStart;
                _callsAtWindowStart = _client.TotalApiCalls;
                _windowStart = now;
                _iterationsInWindow = 0;
                _errorsInWindow = 0;
            }
            _iterationsInWindow++;
            if (!success) _errorsInWindow++;
            ErrorRateLast60s = _iterationsInWindow > 0 ? (double)_errorsInWindow / _iterationsInWindow : 0;
        }

        public WorkerStatus GetStatus()
        {
            return new WorkerStatus
            {
                WorkerId = this.WorkerId,
                SourceFeed = string.IsNullOrEmpty(_client.LastSource) || _client.LastSource == "—" ? this.SourceFeed : _client.LastSource,
                IsActive = this.IsActive,
                RequestsPerMin = this.RequestsPerMin,
                ErrorRateLast60s = this.ErrorRateLast60s,
                QueueDepth = this.QueueDepth,
                ApiLatencyMs = _circuitBreaker.State == CircuitState.Open ? -1 : Math.Round(_client.LastLatencyMs),
                LastActivity = DateTime.UtcNow
            };
        }
    }
}
