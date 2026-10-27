namespace DexScannerBot.Models
{
    public class RugRiskScore
    {
        public int ContractRisk { get; set; }
        public int LiquidityRisk { get; set; }
        public int DeveloperRisk { get; set; }
        public int SocialRisk { get; set; }
        public int TotalScore => Math.Clamp(
            (int)(ContractRisk * 0.35 + LiquidityRisk * 0.30 + DeveloperRisk * 0.20 + SocialRisk * 0.15),
            0, 100);

        public string RiskTier => TotalScore <= 20 ? "SAFE" :
                                  TotalScore <= 40 ? "LOW_RISK" :
                                  TotalScore <= 60 ? "MEDIUM_RISK" :
                                  TotalScore <= 80 ? "HIGH_RISK" : "CRITICAL";

        public bool ShouldBlock => TotalScore >= 81
            || ContractRisk >= 55
            || (ContractRisk >= 40 && LiquidityRisk >= 30);
        public bool ShouldReduceSize => !ShouldBlock && TotalScore >= 41 && TotalScore <= 60;
    }
    public class ContractAnalysis
    {
        public bool IsHoneypot { get; set; }
        public bool IsMintable { get; set; }
        public bool IsFreezable { get; set; }
        public bool HasUnknownExtension { get; set; }
        public bool HasMaliciousOpcodes { get; set; }
        public bool HasProxyPattern { get; set; }
        public bool OwnerCanChangeBalance { get; set; }
        public bool MintAuthorityBurned { get; set; }
        public bool FreezeAuthorityDisabled { get; set; }
        public List<string> SuspiciousPatterns { get; set; } = new();
        public int RiskScore { get; set; }
    }

    public class LiquidityAnalysis
    {
        public bool IsLpLocked { get; set; }
        public string? LockProvider { get; set; }
        public DateTime? LockExpiry { get; set; }
        public double Top10LpConcentration { get; set; }
        public bool HasRugPullSignature { get; set; }
        public bool IsBurnVerified { get; set; }
        public double LpAgeHours { get; set; }
        /// <summary>Detected liquidity drain percentage (0-100). 0 = stable, 60+ = collapsing.</summary>
        public double LiquidityDrainPct { get; set; }
        /// <summary>True when liquidity dropped >20% between consecutive readings.</summary>
        public bool IsLiquidityDraining { get; set; }
        public int RiskScore { get; set; }
    }

    public class DeveloperAnalysis
    {
        public bool IsDevHeavy { get; set; }
        public double DevHoldPercentage { get; set; }
        public bool HasCoordinatedSelling { get; set; }
        public string FundingSource { get; set; } = "unknown";
        public bool IsFromMixer { get; set; }
        public List<string> DevWalletCluster { get; set; } = new();
        /// <summary>True when volume patterns suggest artificial wash trading.</summary>
        public bool IsWashTrading { get; set; }
        /// <summary>0-100 wash trading confidence score.</summary>
        public double WashTradingScore { get; set; }
        /// <summary>True when sells spike 3x+ above buys in the 5m window.</summary>
        public bool HasCoordinatedDump { get; set; }
        public int RiskScore { get; set; }
    }

    public class SocialAnalysis
    {
        public double SentimentScore { get; set; }
        public bool HasBotCampaign { get; set; }
        public bool HasWebsite { get; set; }
        public int DomainAgeDays { get; set; }
        public bool HasTelegramActivity { get; set; }
        /// <summary>True when the token has a Twitter/X social link specifically.</summary>
        public bool HasTwitter { get; set; }
        public int TwitterPostCount24h { get; set; }
        public bool IsCoordinatedCampaign { get; set; }
        /// <summary>True when the only social is Telegram on a brand-new token — common rug pattern.</summary>
        public bool IsTelegramOnlyNewToken { get; set; }
        public List<string> BotPatterns { get; set; } = new();
        public int RiskScore { get; set; }
    }

    public class RugDetectionResult
    {
        public string TokenAddress { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Chain { get; set; } = "";
        public RugRiskScore RiskScore { get; set; } = new();
        public ContractAnalysis Contract { get; set; } = new();
        public LiquidityAnalysis Liquidity { get; set; } = new();
        public DeveloperAnalysis Developer { get; set; } = new();
        public SocialAnalysis Social { get; set; } = new();
        public bool VerifiedReputable { get; set; }
        public string Verification { get; set; } = "";
        public string RecommendedAction { get; set; } = "";
        public List<string> Flags { get; set; } = new();
        public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    }

    public enum ExecutionMode { Auto, Draft, Manual }
    public enum PositionStatus { Open, PartialTP1, PartialTP2, Trailing, Closed }
    public enum TradeSide { BUY, SELL }
    public enum TradeStatus { PENDING, CONFIRMED, FAILED, CANCELLED }

    public class Position
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string TokenAddress { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Chain { get; set; } = "solana";
        public TradeSide Side { get; set; } = TradeSide.BUY;
        public double EntryPrice { get; set; }
        public string ExitPrice { get; set; } = "";
        public double SizeUsd { get; set; }
        public double SizeTokens { get; set; }
        public double CurrentPrice { get; set; }
        public double UnrealizedPnlPct { get; set; }
        public double UnrealizedPnlUsd { get; set; }
        public PositionStatus Status { get; set; } = PositionStatus.Open;
        public int TpLevelReached { get; set; }
        public double TrailingStopPrice { get; set; }
        public double StopLossPct { get; set; } = -0.30;
        public double TrailingStopPct { get; set; } = -0.15;
        public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ClosedAt { get; set; }
        public double RealizedPnl { get; set; }
        public double RealizedPnlPct { get; set; }
        public string? TxHash { get; set; }
        public TradeStatus TradeStatus { get; set; } = TradeStatus.PENDING;
    }

    public class TradeRecord
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string TokenAddress { get; set; } = "";
        public Guid AnalysisId { get; set; }
        public TradeSide Side { get; set; }
        public double SizeSol { get; set; }
        public double SizeUsd { get; set; }
        public string EntryPrice { get; set; } = "";
        public string ExitPrice { get; set; } = "";
        public double RealizedPnl { get; set; }
        public double RealizedPnlPct { get; set; }
        public string? TxHash { get; set; }
        public TradeStatus Status { get; set; }
        public DateTime? ExecutedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class ScanSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? EndedAt { get; set; }
        public int ScannedCount { get; set; }
        public int SurfacedCount { get; set; }
        public int RejectedCount { get; set; }
        public string Source { get; set; } = "";
    }

    public class SystemStatus
    {
        public int WorkersActive { get; set; }
        public int QueueDepth { get; set; }
        public double ApiLatencyMs { get; set; }
        public int TradesToday { get; set; }
        public double PnlToday { get; set; }
        public int UptimeSeconds { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class WorkerStatus
    {
        public int WorkerId { get; set; }
        public string SourceFeed { get; set; } = "";
        public bool IsActive { get; set; }
        public int RequestsPerMin { get; set; }
        public double ErrorRateLast60s { get; set; }
        public int QueueDepth { get; set; }
        public double ApiLatencyMs { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }

    public class ChainConfig
    {
        public string Id { get; set; } = "solana";
        public bool Enabled { get; set; } = true;
        public string RpcUrl { get; set; } = "";
        public string WsUrl { get; set; } = "";
        public string Commitment { get; set; } = "confirmed";
    }

    public class DataSourceConfig
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public int PollIntervalMs { get; set; }
        public string? ApiKey { get; set; }
        public string? WsEndpoint { get; set; }
    }

    public class ExecutionConfig
    {
        public string SolanaProvider { get; set; } = "jupiter";
        public string JupiterUltraUrl { get; set; } = "https://ultra-api.jup.ag/ultra/v1/order";
        public int MaxSlippageBps { get; set; } = 300;
        public int PriorityFeeLamports { get; set; } = 5000;
        public int RetryAttempts { get; set; } = 3;
        public int RetryBackoffMs { get; set; } = 200;
        public string EvmProvider { get; set; } = "0x";
        public string ZeroXApi { get; set; } = "https://api.0x.org/swap/v1/quote";
        public int DeadlineSeconds { get; set; } = 30;
    }

    public class PositionConfig
    {
        public double PartialTp1Pct { get; set; } = 0.25;
        public double PartialTp1Mult { get; set; } = 2.0;
        public double PartialTp2Pct { get; set; } = 0.50;
        public double PartialTp2Mult { get; set; } = 5.0;
        public bool TrailToBreakeven { get; set; } = true;
        public int MaxHoldHoursAuto { get; set; } = 4;
        public int MaxHoldHoursManual { get; set; } = 24;
        public double StopLossPct { get; set; } = -0.30;
        public double TrailingStopPct { get; set; } = -0.15;
    }

    public class DatabaseConfig
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 5432;
        public string Name { get; set; } = "dexscannerbot";
        public string User { get; set; } = "postgres";
        public string Password { get; set; } = "";
        public string SslMode { get; set; } = "require";
        public int PoolMax { get; set; } = 20;
        public int PoolIdle { get; set; } = 30;
    }

    public class RedisConfig
    {
        public string Address { get; set; } = "localhost:6379";
        public string? Password { get; set; }
        public int Db { get; set; } = 0;
        public int PoolSize { get; set; } = 50;
    }

    public class ScannerConfig
    {
        public int Workers { get; set; } = 8;
        public int BatchSize { get; set; } = 30;
        public int PollIntervalMs { get; set; } = 1000;
        public double GatekeepScore { get; set; } = 50.0;
        public int MaxRejectLog { get; set; } = 1000;
    }

    public class FrontendConfig
    {
        public string Host { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 3000;
        public string WsPath { get; set; } = "/ws";
        public string ApiPath { get; set; } = "/api/v1";
    }

    public class MonitoringConfig
    {
        public int PrometheusPort { get; set; } = 9090;
        public string GrafanaUrl { get; set; } = "http://localhost:3001";
        public string AlertmanagerUrl { get; set; } = "http://localhost:9093";
    }

    public class AppConfig
    {
        public string Name { get; set; } = "dexscannerbot";
        public string Version { get; set; } = "4.0.0";
        public ExecutionMode Mode { get; set; } = ExecutionMode.Auto;
        public string Environment { get; set; } = "production";
        public ScannerConfig Scanner { get; set; } = new();
        public List<ChainConfig> Chains { get; set; } = new();
        public List<DataSourceConfig> DataSources { get; set; } = new();
        public ExecutionConfig Execution { get; set; } = new();
        public PositionConfig Position { get; set; } = new();
        public DatabaseConfig Database { get; set; } = new();
        public RedisConfig Redis { get; set; } = new();
        public FrontendConfig Frontend { get; set; } = new();
        public MonitoringConfig Monitoring { get; set; } = new();
        public LoggingConfig Logging { get; set; } = new();
        public PredictionConfig Prediction { get; set; } = new();
    }

    public class LoggingConfig
    {
        public string Level { get; set; } = "info";
        public string Format { get; set; } = "json";
        public string Output { get; set; } = "stdout";
    }

    public class RugAlertMessage
    {
        public Guid Id { get; set; }
        public string TokenId { get; set; } = "";
        public string Symbol { get; set; } = "";
        public int RiskScore { get; set; }
        public string RiskTier { get; set; } = "";
        public RugVectors Vectors { get; set; } = new();
        public string RecommendedAction { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class RugVectors
    {
        public ContractVector Contract { get; set; } = new();
        public LiquidityVector Liquidity { get; set; } = new();
        public DeveloperVector Developer { get; set; } = new();
        public SocialVector Social { get; set; } = new();
    }

    public class ContractVector
    {
        public bool Honeypot { get; set; }
        public bool Mintable { get; set; }
        public bool Freezable { get; set; }
        public bool UnknownExtension { get; set; }
        public bool MaliciousOpcodes { get; set; }
        public bool ProxyPattern { get; set; }
    }

    public class LiquidityVector
    {
        public bool RugSignature { get; set; }
        public double LpConcentration { get; set; }
        public bool IsLocked { get; set; }
        public bool BurnVerified { get; set; }
    }

    public class DeveloperVector
    {
        public bool DevHeavy { get; set; }
        public bool MixedFunding { get; set; }
        public bool CoordinatedSelling { get; set; }
        public bool IsWashTrading { get; set; }
    }

    public class SocialVector
    {
        public bool BotCampaign { get; set; }
        public bool CoordinatedCampaign { get; set; }
        public int ActivityScore { get; set; }
    }

    public class TokenSurfacedMessage
    {
        public Guid Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public string Chain { get; set; } = "";
        public string Dex { get; set; } = "";
        public double TotalScore { get; set; }
        public string ProfitTier { get; set; } = "";
        public string RiskLevel { get; set; } = "";
        public string Verdict { get; set; } = "";
        public string PriceUsd { get; set; } = "";
        public double MarketCap { get; set; }
        public double LiquidityUsd { get; set; }
        public double Vol24h { get; set; }
        public double Vol6h { get; set; }
        public double Vol1h { get; set; }
        public double Vol5m { get; set; }
        public double Change24h { get; set; }
        public double Change6h { get; set; }
        public double Change1h { get; set; }
        public double Change5m { get; set; }
        public double BuyPressure1h { get; set; }
        public double BuyPressure5m { get; set; }
        public double BuyPressure24h { get; set; }
        public double VolumeAcceleration { get; set; }
        public double EstimatedMultiplier { get; set; }
        public string EntrySignal { get; set; } = "";
        public List<string> BullishSignals { get; set; } = new();
        public List<string> WarningSignals { get; set; } = new();
        public List<string> ProfitReasons { get; set; } = new();
        public string Address { get; set; } = "";
        public string PairAddress { get; set; } = "";
        public string ChartUrl { get; set; } = "";
        public bool   VerifiedReputable { get; set; }
        public string Verification { get; set; } = "";
        public int    RugRiskScore { get; set; }
        public double CalibratedScore { get; set; }
        public double PredictionConfidence { get; set; }
        public string PredictionLabel { get; set; } = "";
        public string EntryDecision { get; set; } = "SKIP";
        public double InvestInSeconds { get; set; }
        public string TimingSummary { get; set; } = "";
        public double SuggestedPullbackPct { get; set; }
        public string MomentumPhase { get; set; } = "unknown";
        public List<object>? Breakdown { get; set; }
        public List<object>? PredictionVerdicts { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // ── Spike engine ───────────────────────────────────────────────────
        public double SpikeProbability { get; set; }
        public string SpikeWindow { get; set; } = "";
        public bool ModelCalibrated { get; set; }
        public ExecutionPlan? Plan { get; set; }
        public TapeFeatures? Live { get; set; }
        public List<string> Cautions { get; set; } = new();
        public List<string> RugFlags { get; set; } = new();
        public double AgeHours { get; set; }
        public double Fdv { get; set; }
        public bool IsBoosted { get; set; }
        public double LpLockedPct { get; set; } = -1;
        public double Top10HolderPct { get; set; } = -1;
        public bool MintAuthorityActive { get; set; }
        public bool FreezeAuthorityActive { get; set; }
        public string IntelSource { get; set; } = "";
        /// <summary>False for the first sighting, true for live re-evaluations of a tracked token.</summary>
        public bool IsUpdate { get; set; }

        /// <summary>Builds the dashboard payload from an analysis and its prediction.</summary>
        public static TokenSurfacedMessage From(TokenAnalysis a, PredictionResult? p,
            RugDetectionResult? rug = null, SecurityIntel? intel = null, bool isUpdate = false)
        {
            return new TokenSurfacedMessage
            {
                Id = Guid.NewGuid(),
                Symbol = a.Symbol,
                Name = a.Name,
                Chain = a.Chain,
                Dex = a.Dex,
                TotalScore = a.TotalScore,
                ProfitTier = a.ProfitTier,
                RiskLevel = a.RiskLevel,
                Verdict = a.Verdict,
                PriceUsd = a.PriceUsd,
                MarketCap = a.MarketCap,
                Fdv = a.Fdv,
                LiquidityUsd = a.LiquidityUsd,
                Vol24h = a.Vol24h,
                Vol6h = a.Vol6h,
                Vol1h = a.Vol1h,
                Vol5m = a.Vol5m,
                Change24h = a.Change24h,
                Change6h = a.Change6h,
                Change1h = a.Change1h,
                Change5m = a.Change5m,
                BuyPressure1h = a.BuyPressure1h,
                BuyPressure5m = a.BuyPressure5m,
                BuyPressure24h = a.BuyPressure24h,
                VolumeAcceleration = a.VolumeAcceleration,
                EstimatedMultiplier = a.EstimatedMultiplier,
                EntrySignal = a.EntrySignal,
                BullishSignals = a.BullishSignals,
                WarningSignals = a.WarningSignals,
                ProfitReasons = a.ProfitReasons,
                Address = a.Address ?? "",
                PairAddress = a.PairAddress,
                ChartUrl = a.ChartUrl,
                VerifiedReputable = a.VerifiedReputable,
                Verification = a.Verification,
                RugRiskScore = a.RugRiskScore,
                CalibratedScore = a.CalibratedScore,
                PredictionConfidence = a.PredictionConfidence,
                PredictionLabel = p?.Label ?? a.PredictionLabel,
                EntryDecision = a.EntryDecision,
                InvestInSeconds = a.InvestInSeconds,
                TimingSummary = a.TimingSummary,
                SuggestedPullbackPct = a.SuggestedPullbackPct,
                MomentumPhase = a.MomentumPhase,
                Breakdown = p?.Breakdown.Select(b => (object)new { b.Name, b.Delta, b.Positive, b.Note }).ToList(),
                PredictionVerdicts = p?.Verdicts.Select(v => (object)new { v.Source, v.Vote, v.Detail, v.EffectiveWeight }).ToList(),
                SpikeProbability = p?.SpikeProbability ?? 0,
                SpikeWindow = p?.SpikeWindow ?? "",
                ModelCalibrated = p?.ModelCalibrated ?? false,
                Plan = p?.Plan,
                Live = p?.Live,
                Cautions = p?.Cautions ?? new List<string>(),
                RugFlags = rug?.Flags ?? new List<string>(),
                AgeHours = a.AgeHours,
                IsBoosted = a.IsBoosted,
                LpLockedPct = intel?.LpLockedPct ?? -1,
                Top10HolderPct = intel?.Top10HolderPct ?? -1,
                MintAuthorityActive = intel?.MintAuthorityActive ?? false,
                FreezeAuthorityActive = intel?.FreezeAuthorityActive ?? false,
                IntelSource = intel?.Source ?? "",
                IsUpdate = isUpdate,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    public class TradeExecutedMessage
    {
        public Guid TradeId { get; set; }
        public string TokenId { get; set; } = "";
        public string Side { get; set; } = "";
        public double SizeUsd { get; set; }
        public double SizeSol { get; set; }
        public string EntryPrice { get; set; } = "";
        public string TxHash { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    }

    public class PositionUpdateMessage
    {
        public Guid TradeId { get; set; }
        public string CurrentPrice { get; set; } = "";
        public double UnrealizedPnlPct { get; set; }
        public double UnrealizedPnlUsd { get; set; }
        public int TpLevel { get; set; }
        public double SlDistancePct { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class SystemStatusMessage
    {
        public int WorkersActive { get; set; }
        public int QueueDepth { get; set; }
        public double ApiLatencyMs { get; set; }
        public int TradesToday { get; set; }
        public double PnlToday { get; set; }
        public int UptimeSeconds { get; set; }
        public int ScannedCount { get; set; }
        public string Source { get; set; } = "";
        public List<WorkerStatus> Workers { get; set; } = new();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
