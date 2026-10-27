using System;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using DexScannerBot.Core;
using DexScannerBot.Models;
using DexScannerBot.Services;

namespace DexScannerBot
{
    class Program
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static AppConfig? _config;
        private static WebSocketServer? _wsServer;
        private static DatabaseService? _db;
        private static HistoryApiService? _historyApi;
        private static HeliusWebSocketClient? _helius;
        private static PaperTradingEngine? _paperTrading;
        private static SpikePredictionEngine? _spikeEngine;
        private static SpikeMonitor? _monitor;
        private static OutcomeTracker? _outcomes;
        private static List<ScanWorker>? _workers;
        private static int _surfaced;
        private const int MAX_REJECT_LOG = 1000;
        private static DateTime _startTime;
        private static ScanSession? _currentSession;

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        static async Task<int> Main(string[] args)
        {
            InitConsole();
            PrintBanner();

            _config = new ConfigService().Config;
            _startTime = DateTime.UtcNow;

            Console.WriteLine($"[BOOT] DexScannerBot v{_config.Version} starting in {_config.Mode} mode...");
            Console.WriteLine($"[CONF] Workers: {_config.Scanner.Workers} | Gatekeep: {_config.Scanner.GatekeepScore} | Chains: {string.Join(", ", _config.Chains.Where(c => c.Enabled).Select(c => c.Id))}");

            _workers = new List<ScanWorker>();

            // Initialize Database Service (PostgreSQL with In-Memory fallback)
            try
            {
                Console.WriteLine("[DB] Initializing Database Service...");
                _db = new DatabaseService(_config);
                await _db.InitializeAsync();
                Guid sessionId = await _db.StartScanSessionAsync("dexscreener");
                _currentSession = new Models.ScanSession { Id = sessionId };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] ℹ Storage active in In-Memory mode: {ex.Message}");
            }

            // Initialize History API service
            try
            {
                _historyApi = new HistoryApiService(_config);
                await _historyApi.InitializeAsync();
                if (_historyApi.IsReady)
                    Console.WriteLine("[API] ✓ History API ready");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] ℹ History API initialized with In-Memory store: {ex.Message}");
            }

            // Initialize Paper Trading engine
            _paperTrading = new PaperTradingEngine(_config);
            Console.WriteLine("[PAPER] ✓ Paper trading engine ready ($1000 starting balance)");

            Console.WriteLine("[WS] Starting WebSocket server on " + _config.Frontend.Port + "...");
            var wsServer = new WebSocketServer("127.0.0.1", _config.Frontend.Port);
            wsServer.OnMessageReceived += (addr, msg) => HandleClientMessage(addr, msg.ToString() ?? "");
            wsServer.SetHistoryApi(_historyApi!);
            await wsServer.StartAsync();

            if (wsServer.IsListening)
            {
                _wsServer = wsServer;
                Console.WriteLine("[WS] ✓ HttpListener started successfully");
                Console.WriteLine($"[WS] ✓ Dashboard: http://localhost:{_config.Frontend.Port}");
                Console.WriteLine($"[WS] ✓ API: http://localhost:{_config.Frontend.Port}/api/v1/");

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = $"http://localhost:{_config.Frontend.Port}",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
            else
            {
                Console.WriteLine("[WS] ⚠ Scanning will continue without the web dashboard.");
                _wsServer = null;
                wsServer.Dispose();
            }

            // Initialize Helius WebSocket for real-time pool detection
            _helius = new HeliusWebSocketClient(_config);
            if (_helius.IsEnabled)
            {
                _helius.OnNewPoolDetected += OnHeliusNewPoolDetected;
                _helius.OnError += (type, msg) => Console.WriteLine($"[HELIUS] Error ({type}): {msg}");
                _helius.OnConnected += () => Console.WriteLine("[HELIUS] ✓ WebSocket connected");
                _helius.OnDisconnected += () => Console.WriteLine("[HELIUS] ⚠ WebSocket disconnected");
                await _helius.StartAsync();
            }
            else
            {
                Console.WriteLine("[HELIUS] ⚠ No API key configured - WebSocket disabled (set HELIUS_API_KEY)");
            }

            // ── Spike prediction stack ─────────────────────────────────────
            //   OutcomeTracker  → follows every call, learns, measures calibration
            //   SpikePredictionEngine → probability, phase, timing, wallet execution plan
            //   MarketTape + SpikeMonitor → live re-polling of watched tokens every few seconds
            var outcomes = new OutcomeTracker(_config.Prediction);
            _outcomes = outcomes;
            Console.WriteLine($"[PREDICT] Outcome tracker ready ({outcomes.TotalResolved} resolved, model updates: {outcomes.Model.Updates})");
            outcomes.OnResolved += OnOutcomeResolved;
            _ = Task.Run(() => outcomes.RunLoopAsync(CancellationToken.None));

            _spikeEngine = new SpikePredictionEngine(_config.Prediction, outcomes.Model, () => outcomes.TotalResolved);
            var sharedIntel = new SecurityIntelService();
            var tape = new MarketTape(_config.Prediction);
            _monitor = new SpikeMonitor(tape, _spikeEngine, new AnalysisEngine(_config), sharedIntel, outcomes);
            _monitor.OnUpdate += OnTrackedTokenUpdate;
            _monitor.OnSpikeAlert += OnSpikeAlert;
            _ = Task.Run(() => tape.RunAsync(CancellationToken.None));
            Console.WriteLine($"[PREDICT] ✓ Spike engine active — target {_config.Prediction.WinReturnPct:F0}% before −{_config.Prediction.SpikeStopPct:F0}% within {_config.Prediction.SpikeHorizonMinutes}m, live tape every {_config.Prediction.TapePollSeconds}s");

            // Shared dedup set — all workers share this so a token surfaced by Worker 0
            // is instantly marked as seen by Workers 1-7, preventing 8x duplicate surfacing.
            var sharedSeenSet = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _config.Scanner.Workers; i++)
            {
                var client = new DexScreenerClient(sharedSeenSet, i, _config);
                var engine = new AnalysisEngine(_config);
                var rugDetector = new RugDetectService();
                var worker = new ScanWorker(i, client, engine, rugDetector, _spikeEngine, _wsServer, _config,
                    intelService: sharedIntel, monitor: _monitor, outcomes: outcomes);
                worker.OnTokenSurfaced += OnTokenSurfaced;
                worker.OnRugAlert += OnRugAlert;
                worker.Start();
                _workers.Add(worker);
            }
            Console.WriteLine($"[SCAN] {_config.Scanner.Workers} workers started");

            _ = Task.Run(StatusBroadcastLoopAsync);
            _ = Task.Run(PaperTradingMonitorLoopAsync);

            Console.WriteLine("[SYS] DexScannerBot v4.0 is live — spike radar online. Press Ctrl+C to stop.\n");
            await Task.Delay(-1);
            return 0;
        }

        private static async Task PaperTradingMonitorLoopAsync()
        {
            while (true)
            {
                try
                {
                    if (_paperTrading == null) { await Task.Delay(5000); continue; }
                    
                    // Get current prices for open trades from DexScreener
                    var openTrades = _paperTrading.GetOpenTrades();
                    if (openTrades.Count > 0)
                    {
                        var prices = new Dictionary<string, double>();
                        using var http = new HttpClient();
                        http.Timeout = TimeSpan.FromSeconds(10);
                        
                        foreach (var batch in openTrades.Chunk(30))
                        {
                            var addresses = string.Join(",", batch.Select(t => t.Address));
                            try
                            {
                                var raw = await http.GetStringAsync($"https://api.dexscreener.com/latest/dex/tokens/{addresses}");
                                // Price from each token's deepest pool (the old loop took whichever pair came last).
                                foreach (var kv in DexJson.BestPairPerToken(DexJson.ParsePairs(raw)))
                                {
                                    var price = DexJson.ParsePrice(kv.Value.PriceUsd);
                                    if (price > 0) prices[kv.Key] = price;
                                }
                            }
                            catch { }
                        }

                        _paperTrading.UpdateTrades(prices);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[PAPER] Error: {ex.Message}"); }
                await Task.Delay(5000);
            }
        }

        private static void OnHeliusNewPoolDetected(HeliusPoolEvent poolEvent)
        {
            Console.WriteLine($"[HELIUS] 🏊 New pool: {poolEvent.PoolAddress} via {poolEvent.DexProgram} (sig: {poolEvent.Signature})");
            
            // Broadcast to frontend
            _wsServer?.BroadcastAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "new_pool_detected",
                payload = new
                {
                    poolAddress = poolEvent.PoolAddress,
                    dexProgram = poolEvent.DexProgram,
                    signature = poolEvent.Signature,
                    source = poolEvent.Source,
                    detectedAt = poolEvent.DetectedAt
                }
            }, _jsonOptions));
        }

        private static async Task StatusBroadcastLoopAsync()
            {
                long tick = 0;
                while (true)
                {
                    tick++;
                    try
                    {
                        if (_wsServer == null || _workers == null) { await Task.Delay(1000); continue; }
                        var workersList = _workers.Select(w => w.GetStatus()).ToList();
                        var paperStats = _paperTrading?.GetStats();
                        var status = new Models.SystemStatusMessage
                        {
                            WorkersActive = _config!.Scanner.Workers,
                            QueueDepth = _workers.Sum(w => w.QueueDepth),
                            ApiLatencyMs = Math.Round(workersList.Where(w => w.ApiLatencyMs > 0).Select(w => w.ApiLatencyMs).DefaultIfEmpty(0).Average()),
                            TradesToday = _paperTrading?.TradesToday ?? 0,
                            PnlToday = _paperTrading?.PnlToday ?? 0,
                            UptimeSeconds = (int)(DateTime.UtcNow - _startTime).TotalSeconds,
                            ScannedCount = _workers.Sum(w => w.ScannedCount),
                            Source = "dexscreener",
                            Workers = workersList,
                            Timestamp = DateTime.UtcNow
                        };
                        _ = _wsServer.BroadcastAsync(JsonSerializer.Serialize(new
                        {
                            type = "system_status",
                            payload = status
                        }, _jsonOptions));

                        // Paper-trading and engine stats every ~5 seconds
                        if (paperStats != null && tick % 5 == 1)
                        {
                            _ = _wsServer.BroadcastAsync(JsonSerializer.Serialize(new
                            {
                                type = "paper_trading_stats",
                                payload = new
                                {
                                    balance = paperStats.CurrentBalance,
                                    totalPnl = paperStats.TotalPnl,
                                    totalPnlPct = paperStats.TotalPnlPct,
                                    winRate = paperStats.WinRate,
                                    closedTrades = paperStats.ClosedTrades,
                                    openTrades = paperStats.OpenTrades,
                                    maxDrawdown = paperStats.MaxDrawdown,
                                    sharpeRatio = paperStats.SharpeRatio,
                                    profitFactor = paperStats.ProfitFactor
                                }
                            }, _jsonOptions));

                            // Broadcast prediction-engine accuracy & calibration
                            if (_outcomes != null)
                            {
                                _ = _wsServer.BroadcastAsync(JsonSerializer.Serialize(new
                                {
                                    type = "prediction_stats",
                                    payload = _outcomes.GetStatsPayload(_monitor?.WatchingCount ?? 0)
                                }, _jsonOptions));
                            }
                        }
                    }
                    catch (Exception) when (_wsServer == null)
                    {
                        await Task.Delay(1000);
                    }
                    catch { }
                    await Task.Delay(1000);
                }
            }

        private static void OnTokenSurfaced(ScanWorker worker, TokenAnalysis analysis)
        {
            _surfaced++;
            var p = analysis.Prediction;
            Console.WriteLine($"[SURFACED] {analysis.Symbol} | spike {analysis.CalibratedScore:F0}% | {analysis.EntryDecision} ({analysis.MomentumPhase}) | {p?.SpikeWindow}");
            if (analysis.EntryDecision == "INVEST_NOW" && p != null) AnnounceSpike(analysis, p);
            TryOpenPaperTrade(analysis);
        }

        /// <summary>Live re-evaluation of a tracked token (from the market tape).</summary>
        private static void OnTrackedTokenUpdate(TokenAnalysis a, PredictionResult p, RugDetectionResult rug, SecurityIntel? intel)
        {
            _wsServer?.BroadcastAsync(JsonSerializer.Serialize(new
            {
                type = "token_update",
                payload = TokenSurfacedMessage.From(a, p, rug, intel, isUpdate: true)
            }, _jsonOptions));
        }

        /// <summary>A tracked token just flipped to INVEST_NOW.</summary>
        private static void OnSpikeAlert(TokenAnalysis a, PredictionResult p)
        {
            AnnounceSpike(a, p);
            TryOpenPaperTrade(a);
        }

        private static void AnnounceSpike(TokenAnalysis a, PredictionResult p)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SPIKE] ⚡ {a.Symbol} ({a.Chain}) — {p.Label} · p={p.SpikeProbability * 100:F0}% · {p.MomentumPhase} · window {p.SpikeWindow} · slippage {p.Plan.RecommendedSlippagePct:F1}% · max entry ${p.Plan.MaxEntryPrice:G6} · CA {a.Address}");
            Console.ResetColor();
            _wsServer?.BroadcastAsync(JsonSerializer.Serialize(new
            {
                type = "spike_alert",
                payload = new
                {
                    symbol = a.Symbol,
                    chain = a.Chain,
                    address = a.Address,
                    probability = p.SpikeProbability,
                    label = p.Label,
                    phase = p.MomentumPhase,
                    window = p.SpikeWindow,
                    summary = p.TimingSummary,
                    slippagePct = p.Plan.RecommendedSlippagePct,
                    maxEntry = p.Plan.MaxEntryPrice,
                    timestamp = DateTime.UtcNow
                }
            }, _jsonOptions));
        }

        private static void TryOpenPaperTrade(TokenAnalysis analysis)
        {
            if (_paperTrading == null || analysis.EntryDecision != "INVEST_NOW") return;
            var trade = _paperTrading.OpenTrade(analysis);
            if (trade == null) return;
            _wsServer?.BroadcastAsync(JsonSerializer.Serialize(new
            {
                type = "paper_trade_opened",
                payload = new
                {
                    tradeId = trade.Id,
                    symbol = trade.Symbol,
                    entryPrice = trade.EntryPrice,
                    sizeUsd = trade.SizeUsd,
                    score = trade.ScoreAtEntry,
                    probability = trade.SpikeProbability,
                    stopLoss = trade.StopLossPrice,
                    takeProfit = trade.TakeProfitPrice
                }
            }, _jsonOptions));
        }

        private static void OnOutcomeResolved(TrackedOutcome t)
        {
            _wsServer?.BroadcastAsync(JsonSerializer.Serialize(new
            {
                type = "outcome_resolved",
                payload = new
                {
                    symbol = t.Symbol,
                    chain = t.Chain,
                    address = t.Address,
                    probability = t.Probability,
                    decision = t.Decision,
                    label = t.Label,
                    maxReturnPct = Math.Round(t.MaxReturnPct, 1),
                    minReturnPct = Math.Round(t.MinReturnPct, 1),
                    reason = t.Reason
                }
            }, _jsonOptions));
        }

        private static void OnRugAlert(ScanWorker worker, RugDetectionResult rug)
        {
            Console.WriteLine($"[RUG ALERT] {rug.Symbol} | Risk: {rug.RiskScore.TotalScore}/100 | {rug.RiskScore.RiskTier} | {string.Join(", ", rug.Flags)}");
        }

        private static void HandleClientMessage(string addr, string msg)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(msg);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? "";
                    if (type == "toggle_mode" && root.TryGetProperty("payload", out var payload))
                    {
                        string mode = payload.GetProperty("mode").GetString() ?? "auto";
                        if (_config != null && Enum.TryParse<ExecutionMode>(mode, true, out var em))
                        {
                            _config.Mode = em;
                            Console.WriteLine($"[CFG] Mode changed to {mode}");
                        }
                    }
                }
            }
            catch { }
        }

        static void PrintBanner()
        {
            try { Console.Clear(); } catch { }
            var lines = new (string, ConsoleColor)[]
            {
                ("", ConsoleColor.White),
                (" ██╗   ██╗██╗██╗██████╗ ███████╗", ConsoleColor.Red),
                (" ██║   ██║██║██║██╔══██╗██╔════╝", ConsoleColor.Yellow),
                (" ██║   ██║██║██║██████╔╝█████╗  ", ConsoleColor.Green),
                (" ╚██╗ ██╔╝██║██║██╔══██╗██╔══╝  ", ConsoleColor.Cyan),
                ("  ╚████╔╝ ██║██║██████╔╝███████╗", ConsoleColor.Magenta),
                ("   ╚═══╝  ╚═╝╚═╝╚═════╝ ╚══════╝", ConsoleColor.Blue),
                ("", ConsoleColor.White),
                (" ██████╗ ██████╗ ███████╗██████╗ ██╗ ██████╗████████╗██╗ ██████╗ ███╗   ██╗", ConsoleColor.Red),
                (" ██╔══██╗██╔══██╗██╔════╝██╔══██╗██║██╔════╝╚══██╔══╝██║██╔═══██╗████╗  ██║", ConsoleColor.Yellow),
                (" ██████╔╝██████╔╝█████╗  ██║  ██║██║██║        ██║   ██║██║   ██║██╔██╗ ██║", ConsoleColor.Green),
                (" ██╔═══╝ ██╔══██╗██╔══╝  ██║  ██║██║██║        ██║   ██║██║   ██║██║╚██╗██║", ConsoleColor.Cyan),
                (" ██║     ██║  ██║███████╗██████╔╝██║╚██████╗   ██║   ██║╚██████╔╝██║ ╚████║", ConsoleColor.Magenta),
                (" ╚═╝     ╚═╝  ╚═╝╚══════╝╚═════╝ ╚═╝ ╚═════╝   ╚═╝   ╚═╝ ╚═════╝ ╚═╝  ╚═══╝", ConsoleColor.Blue),
                ("", ConsoleColor.White),
                ("   V4.0  |  HIGH-FREQUENCY SNIPING ENGINE  |  Sub-100ms Signal-to-Trade", ConsoleColor.DarkCyan),
                ("   Rug Detection | Multi-Chain | WebSocket | PostgreSQL | Parallel Workers", ConsoleColor.DarkMagenta),
                (" ─────────────────────────────────────────────────────────────────────────────────", ConsoleColor.DarkGray),
                ("", ConsoleColor.White),
            };
            foreach (var (text, color) in lines)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(text);
            }
            Console.ResetColor();
        }

        static void InitConsole()
        {
            try { Console.Title = "DexScannerBot v4.0 — High-Frequency Sniping Engine"; } catch { }
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            try
            {
#pragma warning disable CA1416
                int w = 160, h = 55;
                Console.BufferWidth = w;
                Console.BufferHeight = 3000;
                int ww = Math.Min(w, Console.LargestWindowWidth > 0 ? Console.LargestWindowWidth : w);
                int wh = Math.Min(h, Console.LargestWindowHeight > 0 ? Console.LargestWindowHeight : h);
                Console.WindowWidth = ww;
                Console.WindowHeight = wh;
#pragma warning restore CA1416
            }
            catch { }
            CenterConsoleWindow();
        }

        static void CenterConsoleWindow()
        {
            try
            {
                IntPtr hWnd = GetConsoleWindow();
                if (hWnd == IntPtr.Zero) return;
                RECT rect;
                if (!GetWindowRect(hWnd, out rect)) return;
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int screenW = GetSystemMetrics(SM_CXSCREEN);
                int screenH = GetSystemMetrics(SM_CYSCREEN);
                SetWindowPos(hWnd, IntPtr.Zero,
                    (screenW - width) / 2, (screenH - height) / 2,
                    0, 0, SWP_NOSIZE | SWP_NOZORDER);
            }
            catch { }
        }
    }
}
