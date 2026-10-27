using System.Text.Json;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    public class ConfigService
    {
        private AppConfig _config;
        private readonly string _configPath;
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public AppConfig Config => _config;

        public ConfigService(string configPath = "config/default.yaml")
        {
            _configPath = configPath;
            _config = LoadConfig();
        }

        private AppConfig LoadConfig()
        {
            var env = Environment.GetEnvironmentVariable("DEXSB_ENV") ?? "production";

            _config = new AppConfig
            {
                Name = GetEnv("DEXSB_NAME", "dexscannerbot"),
                Version = GetEnv("DEXSB_VERSION", "4.0.0"),
                Mode = Enum.TryParse<ExecutionMode>(GetEnv("DEXSB_MODE", "auto"), true, out var mode) ? mode : ExecutionMode.Auto,
                Environment = env,
                Scanner = new ScannerConfig
                {
                    Workers = int.TryParse(GetEnv("DEXSB_SCANNER_WORKERS"), out var w) ? w : 8,
                    BatchSize = int.TryParse(GetEnv("DEXSB_SCANNER_BATCH_SIZE"), out var bs) ? bs : 30,
                    PollIntervalMs = int.TryParse(GetEnv("DEXSB_SCANNER_POLL_MS"), out var pi) ? pi : 1000,
                    // Lowered from 60: the spike engine now judges quiet accumulation setups that the
                    // momentum-heavy heuristic score used to reject before they ever moved.
                    GatekeepScore = double.TryParse(GetEnv("DEXSB_GATEKEEP_SCORE"), out var gs) ? gs : 50.0,
                    MaxRejectLog = int.TryParse(GetEnv("DEXSB_MAX_REJECT_LOG"), out var mrl) ? mrl : 1000
                },
                Chains = new List<ChainConfig>
                {
                    new ChainConfig
                    {
                        Id = "solana",
                        Enabled = bool.Parse(GetEnv("DEXSB_SOLANA_ENABLED", "true")),
                        RpcUrl = GetEnv("DEXSB_SOLANA_RPC", "https://api.mainnet-beta.solana.com"),
                        WsUrl = GetEnv("DEXSB_SOLANA_WS", "wss://api.mainnet-beta.solana.com"),
                        Commitment = GetEnv("DEXSB_SOLANA_COMMITMENT", "confirmed")
                    },
                    new ChainConfig
                    {
                        Id = "ethereum",
                        Enabled = bool.Parse(GetEnv("DEXSB_ETH_ENABLED", "true")),
                        RpcUrl = GetEnv("DEXSB_ETH_RPC", ""),
                        WsUrl = GetEnv("DEXSB_ETH_WS", "")
                    },
                    new ChainConfig
                    {
                        Id = "base",
                        Enabled = bool.Parse(GetEnv("DEXSB_BASE_ENABLED", "true")),
                        RpcUrl = GetEnv("DEXSB_BASE_RPC", ""),
                        WsUrl = GetEnv("DEXSB_BASE_WS", "")
                    }
                },
                DataSources = new List<DataSourceConfig>
                {
                    new DataSourceConfig
                    {
                        Name = "dexscreener",
                        Enabled = bool.Parse(GetEnv("DEXSB_DS_DEXSCREENER_ENABLED", "true")),
                        PollIntervalMs = int.TryParse(GetEnv("DEXSB_DS_DEXSCREENER_POLL_MS"), out var dp) ? dp : 5000
                    },
                    new DataSourceConfig
                    {
                        Name = "birdeye",
                        Enabled = bool.Parse(GetEnv("DEXSB_DS_BIRDEYE_ENABLED", "false")),
                        ApiKey = GetEnv("BIRDEYE_API_KEY", ""),
                        WsEndpoint = GetEnv("DEXSB_DS_BIRDEYE_WS", "wss://public-api.birdeye.so/solana/token")
                    },
                    new DataSourceConfig
                    {
                        Name = "helius",
                        Enabled = bool.Parse(GetEnv("DEXSB_DS_HELIUS_ENABLED", "false")),
                        ApiKey = GetEnv("HELIUS_API_KEY", ""),
                        WsEndpoint = GetEnv("DEXSB_DS_HELIUS_WS", "")
                    },
                    new DataSourceConfig
                    {
                        Name = "alchemy",
                        Enabled = bool.Parse(GetEnv("DEXSB_DS_ALCHEMY_ENABLED", "false")),
                        ApiKey = GetEnv("ALCHEMY_API_KEY", ""),
                        WsEndpoint = GetEnv("DEXSB_DS_ALCHEMY_WS", "")
                    }
                },
                Execution = new ExecutionConfig
                {
                    SolanaProvider = GetEnv("DEXSB_EXEC_SOL_PROVIDER", "jupiter"),
                    JupiterUltraUrl = GetEnv("DEXSB_JUPITER_ULTRA_URL", "https://ultra-api.jup.ag/ultra/v1/order"),
                    MaxSlippageBps = int.TryParse(GetEnv("DEXSB_MAX_SLIPPAGE_BPS"), out var msb) ? msb : 300,
                    PriorityFeeLamports = int.TryParse(GetEnv("DEXSB_PRIORITY_FEE_LAMPORTS"), out var pfl) ? pfl : 5000,
                    RetryAttempts = int.TryParse(GetEnv("DEXSB_RETRY_ATTEMPTS"), out var ra) ? ra : 3,
                    RetryBackoffMs = int.TryParse(GetEnv("DEXSB_RETRY_BACKOFF_MS"), out var rbm) ? rbm : 200,
                    EvmProvider = GetEnv("DEXSB_EXEC_EVM_PROVIDER", "0x"),
                    ZeroXApi = GetEnv("DEXSB_ZERO_X_API", "https://api.0x.org/swap/v1/quote"),
                    DeadlineSeconds = int.TryParse(GetEnv("DEXSB_DEADLINE_SECONDS"), out var ds) ? ds : 30
                },
                Position = new PositionConfig
                {
                    PartialTp1Pct = double.TryParse(GetEnv("DEXSB_TP1_PCT"), out var tp1) ? tp1 : 0.25,
                    PartialTp1Mult = double.TryParse(GetEnv("DEXSB_TP1_MULT"), out var t1m) ? t1m : 2.0,
                    PartialTp2Pct = double.TryParse(GetEnv("DEXSB_TP2_PCT"), out var tp2) ? tp2 : 0.50,
                    PartialTp2Mult = double.TryParse(GetEnv("DEXSB_TP2_MULT"), out var t2m) ? t2m : 5.0,
                    TrailToBreakeven = bool.Parse(GetEnv("DEXSB_TRAIL_BREAKEVEN", "true")),
                    MaxHoldHoursAuto = int.TryParse(GetEnv("DEXSB_MAX_HOLD_AUTO"), out var mha) ? mha : 4,
                    MaxHoldHoursManual = int.TryParse(GetEnv("DEXSB_MAX_HOLD_MANUAL"), out var mhm) ? mhm : 24,
                    StopLossPct = double.TryParse(GetEnv("DEXSB_STOP_LOSS_PCT"), out var slp) ? slp : -0.30,
                    TrailingStopPct = double.TryParse(GetEnv("DEXSB_TRAILING_STOP_PCT"), out var tsp) ? tsp : -0.15
                },
                Database = new DatabaseConfig
                {
                    Host = GetEnv("DEXSB_DB_HOST", "localhost"),
                    Port = int.TryParse(GetEnv("DEXSB_DB_PORT"), out var dbp) ? dbp : 5432,
                    Name = GetEnv("DEXSB_DB_NAME", "dexscannerbot"),
                    User = GetEnv("DEXSB_DB_USER", "postgres"),
                    Password = GetEnv("DEXSB_DB_PASSWORD", ""),
                    SslMode = GetEnv("DEXSB_DB_SSL_MODE", "require"),
                    PoolMax = int.TryParse(GetEnv("DEXSB_DB_POOL_MAX"), out var pm) ? pm : 20,
                    PoolIdle = int.TryParse(GetEnv("DEXSB_DB_POOL_IDLE"), out var pid) ? pid : 30
                },
                Redis = new RedisConfig
                {
                    Address = GetEnv("DEXSB_REDIS_ADDR", "localhost:6379"),
                    Password = GetEnv("DEXSB_REDIS_PASSWORD"),
                    Db = int.TryParse(GetEnv("DEXSB_REDIS_DB"), out var rdb) ? rdb : 0,
                    PoolSize = int.TryParse(GetEnv("DEXSB_REDIS_POOL"), out var rp) ? rp : 50
                },
                Frontend = new FrontendConfig
                {
                    Host = GetEnv("DEXSB_FRONTEND_HOST", "localhost"),
                    Port = int.TryParse(GetEnv("DEXSB_FRONTEND_PORT"), out var fp) ? fp : 3000,
                    WsPath = GetEnv("DEXSB_FRONTEND_WS", "/ws"),
                    ApiPath = GetEnv("DEXSB_FRONTEND_API", "/api/v1")
                },
                Monitoring = new MonitoringConfig
                {
                    PrometheusPort = int.TryParse(GetEnv("DEXSB_PROMETHEUS_PORT"), out var pp) ? pp : 9090,
                    GrafanaUrl = GetEnv("DEXSB_GRAFANA_URL", "http://localhost:3001"),
                    AlertmanagerUrl = GetEnv("DEXSB_ALERTMANAGER_URL", "http://localhost:9093")
                },
                Logging = new LoggingConfig
                {
                    Level = GetEnv("DEXSB_LOG_LEVEL", "info"),
                    Format = GetEnv("DEXSB_LOG_FORMAT", "json"),
                    Output = GetEnv("DEXSB_LOG_OUTPUT", "stdout")
                },
                Prediction = new PredictionConfig
                {
                    Enabled = bool.Parse(GetEnv("DEXSB_PREDICT_ENABLED", "true")),
                    WinReturnPct = double.TryParse(GetEnv("DEXSB_PREDICT_WIN_PCT"), out var wp) ? wp : 25.0,
                    LossReturnPct = double.TryParse(GetEnv("DEXSB_PREDICT_LOSS_PCT"), out var lp) ? lp : -30.0,
                    MinSamplesForLearning = int.TryParse(GetEnv("DEXSB_PREDICT_MIN_SAMPLES"), out var msl) ? msl : 8,
                    ProbeWebPresence = bool.Parse(GetEnv("DEXSB_PREDICT_WEB_PROBE", "true")),
                    SpikeStopPct = double.TryParse(GetEnv("DEXSB_SPIKE_STOP_PCT"), out var ssp) ? ssp : 15.0,
                    SpikeHorizonMinutes = int.TryParse(GetEnv("DEXSB_SPIKE_HORIZON_MIN"), out var shm) ? shm : 30,
                    CalibrationMinSamples = int.TryParse(GetEnv("DEXSB_CALIBRATION_MIN"), out var cms) ? cms : 30,
                    ReferenceTradeUsd = double.TryParse(GetEnv("DEXSB_TRADE_SIZE_USD"), out var rts) ? rts : 100.0,
                    PhantomSwapFeePct = double.TryParse(GetEnv("DEXSB_PHANTOM_FEE_PCT"), out var pfp) ? pfp : 0.85,
                    InvestThreshold = double.TryParse(GetEnv("DEXSB_INVEST_THRESHOLD"), out var ith) ? ith : 0.55,
                    TapePollSeconds = int.TryParse(GetEnv("DEXSB_TAPE_POLL_SECONDS"), out var tps) ? tps : 8,
                    MaxWatched = int.TryParse(GetEnv("DEXSB_MAX_WATCHED"), out var mxw) ? mxw : 120
                }
            };

            return _config;
        }

        private static string GetEnv(string key, string defaultVal = "") =>
            Environment.GetEnvironmentVariable(key) ?? defaultVal;

        public string? GetDataSourceApiKey(string name) =>
            _config.DataSources.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ApiKey;

        public bool IsChainEnabled(string chainId) =>
            _config.Chains.Any(c => c.Id.Equals(chainId, StringComparison.OrdinalIgnoreCase) && c.Enabled);

        public ChainConfig? GetChainConfig(string chainId) =>
            _config.Chains.FirstOrDefault(c => c.Id.Equals(chainId, StringComparison.OrdinalIgnoreCase));

        public void Reload()
        {
            _config = LoadConfig();
        }
    }
}
