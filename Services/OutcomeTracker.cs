using System.Collections.Concurrent;
using System.Text.Json;
using DexScannerBot.Core;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    /// <summary>
    /// Closes the prediction loop. Every prediction that isn't scam-blocked is followed
    /// for SpikeHorizonMinutes; price is sampled every 20s against the *effective* entry
    /// (spot + estimated price impact, i.e. what a wallet would really pay):
    ///   • reaches +WinReturnPct before −SpikeStopPct → spike (label 1)
    ///   • hits the stop first, collapses, vanishes, or times out → no spike (label 0)
    /// Each resolution trains the SpikeModel and updates calibration buckets so the
    /// dashboard can show, for example, "calls rated 60–70% actually spiked 58% of the time".
    /// State persists across restarts in PredictionConfig.StatePath.
    ///
    /// (The previous version requested /tokens/v1/…, which returns a JSON array, and then
    /// read it as an object — every poll threw silently and nothing was ever resolved.)
    /// </summary>
    public class OutcomeTracker
    {
        private const int MaxPending = 600;
        private static readonly JsonSerializerOptions StateJson = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

        private readonly PredictionConfig _cfg;
        private readonly ConcurrentDictionary<string, TrackedOutcome> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _fetchLock = new(1, 1);
        private readonly object _stateLock = new();
        private DateTime _lastFetch = DateTime.MinValue;
        private readonly string _statePath;
        private PredictionState _state = new();

        public SpikeModel Model { get; }
        public event Action<TrackedOutcome>? OnResolved;

        public int PendingCount => _pending.Count;
        public int TotalResolved => _state.TotalResolved;
        public int TotalWins => _state.TotalWins;
        public bool IsCalibrated => _state.TotalResolved >= _cfg.CalibrationMinSamples;

        public OutcomeTracker(PredictionConfig cfg)
        {
            _cfg = cfg;
            _statePath = cfg.StatePath;
            _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
            _http.DefaultRequestHeaders.Add("User-Agent", "DexScannerBot/4.0 (Win64)");
            _http.Timeout = TimeSpan.FromSeconds(10);
            Load();
            Model = new SpikeModel(_state.HasModel ? _state.Weights : null, _state.HasModel ? _state.Bias : null, _state.ModelUpdates);
        }

        /// <summary>Starts following a prediction (ignored if this token is already being followed).</summary>
        public void Record(TokenAnalysis a, PredictionResult p)
        {
            if (!_cfg.Enabled || p.IsScamBlocked || string.IsNullOrEmpty(a.Address)) return;
            double price = DexJson.ParsePrice(a.PriceUsd);
            if (price <= 0 || p.Features.Count == 0) return;

            string key = MarketTape.KeyOf(a.Chain, a.Address);
            if (_pending.ContainsKey(key)) return;

            if (_pending.Count >= MaxPending)
            {
                foreach (var old in _pending.Values.OrderBy(t => t.SurfacedAtUtc).Take(_pending.Count - MaxPending + 1).ToList())
                    _pending.TryRemove(MarketTape.KeyOf(old.Chain, old.Address), out _);
            }

            var now = DateTime.UtcNow;
            _pending[key] = new TrackedOutcome
            {
                Address = a.Address,
                PairAddress = a.PairAddress,
                Chain = a.Chain,
                Symbol = a.Symbol,
                EntryPrice = price,
                EffectiveEntry = price * (1 + Math.Max(0, p.Plan.PriceImpactPct) / 100.0),
                Probability = p.SpikeProbability,
                Decision = p.EntryDecision,
                Phase = p.MomentumPhase,
                ScoreAtEntry = a.TotalScore,
                SurfacedAtUtc = now,
                LastSeenUtc = now,
                Features = new Dictionary<string, double>(p.Features, StringComparer.OrdinalIgnoreCase),
                LiquidityAtEntry = a.LiquidityUsd
            };
        }

        public async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await PollAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[OUTCOME] Error: {ex.Message}"); }
                try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task PollAsync(CancellationToken ct)
        {
            if (_pending.IsEmpty) return;

            foreach (var group in _pending.Values.Where(t => !t.Resolved).GroupBy(t => t.Chain.ToLowerInvariant()).ToList())
            {
                foreach (var batch in group.Chunk(30))
                {
                    string raw;
                    await _fetchLock.WaitAsync(ct);
                    try
                    {
                        var since = DateTime.UtcNow - _lastFetch;
                        if (since < TimeSpan.FromMilliseconds(300)) await Task.Delay(TimeSpan.FromMilliseconds(300) - since, ct);
                        var url = $"https://api.dexscreener.com/tokens/v1/{group.Key}/{string.Join(",", batch.Select(b => b.Address))}";
                        raw = await _http.GetStringAsync(url, ct);
                        _lastFetch = DateTime.UtcNow;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { continue; } // transient API failure — retry next cycle
                    finally { _fetchLock.Release(); }

                    var pairs = DexJson.ParsePairs(raw);
                    var now = DateTime.UtcNow;
                    foreach (var t in batch)
                    {
                        var mine = pairs.Where(p => string.Equals(p.BaseToken?.Address, t.Address, StringComparison.OrdinalIgnoreCase)).ToList();
                        var pair = mine.FirstOrDefault(p => string.Equals(p.PairAddress, t.PairAddress, StringComparison.OrdinalIgnoreCase))
                                   ?? mine.OrderByDescending(p => p.Liquidity?.Usd ?? 0).FirstOrDefault();
                        double elapsedMin = (now - t.SurfacedAtUtc).TotalMinutes;

                        double price = pair != null ? DexJson.ParsePrice(pair.PriceUsd) : 0;
                        if (price <= 0 || t.EffectiveEntry <= 0)
                        {
                            // Pair vanished (delisted / liquidity removed) for 10+ minutes → failed call.
                            if ((now - t.LastSeenUtc).TotalMinutes >= 10) Resolve(t, 0, "pair vanished");
                            else if (elapsedMin >= _cfg.SpikeHorizonMinutes + 10) Resolve(t, 0, "no data");
                            continue;
                        }

                        t.LastSeenUtc = now;
                        double ret = (price / t.EffectiveEntry - 1.0) * 100.0;
                        t.LastReturnPct = ret;
                        t.MaxReturnPct = Math.Max(t.MaxReturnPct, ret);
                        t.MinReturnPct = Math.Min(t.MinReturnPct, ret);

                        if (ret >= _cfg.WinReturnPct) Resolve(t, 1, $"hit +{_cfg.WinReturnPct:F0}% in {elapsedMin:F1}m");
                        else if (ret <= -50) Resolve(t, 0, "collapsed (rug)");
                        else if (ret <= -_cfg.SpikeStopPct) Resolve(t, 0, $"stopped −{_cfg.SpikeStopPct:F0}% first");
                        else if (elapsedMin >= _cfg.SpikeHorizonMinutes) Resolve(t, 0, $"no spike in {_cfg.SpikeHorizonMinutes}m");
                    }
                }
            }
        }

        private void Resolve(TrackedOutcome t, int label, string reason)
        {
            if (t.Resolved) return;
            t.Resolved = true;
            t.Label = label;
            t.Reason = reason;
            t.ResolvedAtUtc = DateTime.UtcNow;
            _pending.TryRemove(MarketTape.KeyOf(t.Chain, t.Address), out _);

            Model.Update(t.Features, label);
            lock (_stateLock)
            {
                _state.TotalResolved++;
                if (label == 1) _state.TotalWins++;
                if (t.Decision == "INVEST_NOW")
                {
                    _state.ActionableResolved++;
                    if (label == 1) _state.ActionableWins++;
                }
                int b = Math.Clamp((int)(t.Probability * 10), 0, 9);
                _state.BucketCount[b]++;
                if (label == 1) _state.BucketHits[b]++;
                _state.BrierSum += (t.Probability - label) * (t.Probability - label);

                _state.Recent.Insert(0, new ResolvedSummary
                {
                    Symbol = t.Symbol, Chain = t.Chain, Address = t.Address,
                    Probability = t.Probability, Decision = t.Decision, Label = label,
                    MaxReturnPct = Math.Round(t.MaxReturnPct, 1), MinReturnPct = Math.Round(t.MinReturnPct, 1),
                    Reason = reason, ResolvedAtUtc = t.ResolvedAtUtc ?? DateTime.UtcNow
                });
                if (_state.Recent.Count > 25) _state.Recent.RemoveRange(25, _state.Recent.Count - 25);

                var (w, bias) = Model.Export();
                _state.Weights = w;
                _state.Bias = bias;
                _state.HasModel = true;
                _state.ModelUpdates = Model.Updates;
            }
            Save();

            string status = label == 1 ? "SPIKE ✅" : "NO SPIKE ❌";
            Console.WriteLine($"[OUTCOME] {t.Symbol} {status} — predicted {t.Probability * 100:F0}% ({t.Decision}), best {t.MaxReturnPct:+0.0;-0.0}%, worst {t.MinReturnPct:+0.0;-0.0}% · {reason}");
            try { OnResolved?.Invoke(t); } catch { }
        }

        /// <summary>Snapshot of engine accuracy for the dashboard.</summary>
        public object GetStatsPayload(int watching)
        {
            lock (_stateLock)
            {
                var buckets = Enumerable.Range(0, 10).Select(i => new CalibrationBucket
                {
                    Lo = i / 10.0,
                    Hi = (i + 1) / 10.0,
                    Count = _state.BucketCount[i],
                    Hits = _state.BucketHits[i]
                }).ToList();

                return new
                {
                    totalResolved = _state.TotalResolved,
                    totalWins = _state.TotalWins,
                    pendingCount = _pending.Count,
                    // kept for older dashboards: % of resolved predictions that spiked
                    outcomeWinRate = _state.TotalResolved > 0 ? 100.0 * _state.TotalWins / _state.TotalResolved : 0,
                    samples = _state.TotalResolved,
                    calibrated = IsCalibrated,
                    calibrationTarget = _cfg.CalibrationMinSamples,
                    brier = _state.TotalResolved > 0 ? Math.Round(_state.BrierSum / _state.TotalResolved, 4) : 0,
                    actionableResolved = _state.ActionableResolved,
                    actionableHitRate = _state.ActionableResolved > 0 ? 100.0 * _state.ActionableWins / _state.ActionableResolved : 0,
                    buckets = buckets.Select(b => new { lo = b.Lo, hi = b.Hi, count = b.Count, hits = b.Hits, hitRate = Math.Round(b.HitRate * 100, 1) }),
                    drift = Model.Drift(6).Select(d => new
                    {
                        name = SpikeModel.DisplayNames.TryGetValue(d.Name, out var dn) ? dn : d.Name,
                        prior = Math.Round(d.Prior, 3),
                        learned = Math.Round(d.Learned, 3)
                    }),
                    recent = _state.Recent.Take(10).ToList(),
                    spikeDefinition = $"+{_cfg.WinReturnPct:F0}% before −{_cfg.SpikeStopPct:F0}% within {_cfg.SpikeHorizonMinutes}m",
                    watching
                };
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_statePath)) return;
                var loaded = JsonSerializer.Deserialize<PredictionState>(File.ReadAllText(_statePath), StateJson);
                if (loaded == null || loaded.Version < 2)
                {
                    Console.WriteLine("[PREDICT] Old prediction state found — starting the new spike model fresh.");
                    return;
                }
                if (loaded.BucketCount == null || loaded.BucketCount.Length != 10) loaded.BucketCount = new int[10];
                if (loaded.BucketHits == null || loaded.BucketHits.Length != 10) loaded.BucketHits = new int[10];
                loaded.Weights ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                loaded.Recent ??= new List<ResolvedSummary>();
                _state = loaded;
                Console.WriteLine($"[PREDICT] Loaded spike model: {_state.TotalResolved} outcomes resolved, {_state.ModelUpdates} learning updates");
            }
            catch (Exception ex) { Console.WriteLine($"[PREDICT] State load failed: {ex.Message}"); }
        }

        private void Save()
        {
            try
            {
                string json;
                lock (_stateLock) json = JsonSerializer.Serialize(_state, StateJson);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_statePath))!);
                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _statePath, overwrite: true);
            }
            catch { /* persistence is best-effort */ }
        }
    }
}
