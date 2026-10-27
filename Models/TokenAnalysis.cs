namespace DexScannerBot.Models
{
    /// <summary>
    /// Full analysis result for a token that passed every gate.
    /// Only tokens with genuine profit potential reach this stage.
    /// </summary>
    public class TokenAnalysis
    {
        // ── Identity ──────────────────────────────────────────────────────
        public string Name        { get; set; } = "";
        public string Symbol      { get; set; } = "";
        public string Address     { get; set; } = "";
        public string PairAddress { get; set; } = "";
        public string Chain       { get; set; } = "";
        public string Dex         { get; set; } = "";
        public string ChartUrl    { get; set; } = "";
        public string Description { get; set; } = "";

        // ── Market ────────────────────────────────────────────────────────
        public string PriceUsd    { get; set; } = "";
        public double MarketCap   { get; set; }
        public double Fdv         { get; set; }
        public double LiquidityUsd { get; set; }

        // ── Volume ────────────────────────────────────────────────────────
        public double Vol24h { get; set; }
        public double Vol6h  { get; set; }
        public double Vol1h  { get; set; }
        public double Vol5m  { get; set; }

        // ── Price Change ──────────────────────────────────────────────────
        public double Change24h { get; set; }
        public double Change6h  { get; set; }
        public double Change1h  { get; set; }
        public double Change5m  { get; set; }

        // ── Transactions ──────────────────────────────────────────────────
        public int Buys24h  { get; set; }
        public int Sells24h { get; set; }
        public int Buys1h   { get; set; }
        public int Sells1h  { get; set; }
        public int Buys5m   { get; set; }
        public int Sells5m  { get; set; }

        // ── Computed Indicators ───────────────────────────────────────────
        public double VolumeToLiqRatio   { get; set; }
        public double BuyPressure24h     { get; set; }
        public double BuyPressure1h      { get; set; }
        public double BuyPressure5m      { get; set; }
        public double VolumeAcceleration { get; set; }
        public double MomentumScore      { get; set; }
        public double FdvToLiqRatio      { get; set; }
        public double TxnVelocity1h      { get; set; }
        public double AgeHours           { get; set; }
        public int    SocialPresence     { get; set; }
        public bool   IsBoosted          { get; set; }

        // ── Profit Intelligence ──────────────────────────────────────────
        public double EstimatedMultiplier    { get; set; }  // Realistic X target
        public double ConservativeTarget     { get; set; }  // Low-end X
        public double AggressiveTarget       { get; set; }  // High-end X
        public string EntrySignal            { get; set; } = "";  // NOW / WAIT / SKIP
        public string SlippageRisk           { get; set; } = "";  // LOW / MED / HIGH
        public double ProfitConfidence       { get; set; }  // 0-100 how confident we are
        public bool   VerifiedReputable       { get; set; }
        public string Verification           { get; set; } = "";
        public int    RugRiskScore           { get; set; }
        public bool   IsParabolicSetup       { get; set; }
        public bool   IsVolumeAccelerating   { get; set; }
        public bool   IsBuyDominant          { get; set; }
        public bool   HasMomentumStack       { get; set; }  // 5m/1h/6h all positive

        // ── Scoring ───────────────────────────────────────────────────────
        public double TotalScore             { get; set; }  // 0-100
        public string ProfitTier            { get; set; } = "";  // NUKE / MOON / STRONG / WATCH
        public string RiskLevel              { get; set; } = "";
        public string Verdict                { get; set; } = "";

        // ── Signals ───────────────────────────────────────────────────────
        public List<string> BullishSignals   { get; set; } = new();
        public List<string> WarningSignals   { get; set; } = new();
        public List<string> ProfitReasons    { get; set; } = new();

        // ── Multi-Source Prediction ────────────────────────────────────────
        public List<SourceVerdict> PredictionVerdicts { get; set; } = new();
        public double CalibratedScore                 { get; set; }
        public double PredictionConfidence            { get; set; }
        public string PredictionLabel                 { get; set; } = "";
        public string EntryDecision                   { get; set; } = "SKIP";
        public double InvestInSeconds                 { get; set; }
        public string TimingSummary                   { get; set; } = "";
        public double SuggestedPullbackPct            { get; set; }
        public string MomentumPhase                   { get; set; } = "unknown";
        public List<SignalContribution> Breakdown     { get; set; } = new();

        // ── Scam Detection ────────────────────────────────────────────────
        /// <summary>True when the prediction engine hard-blocked this token as a confirmed scam.</summary>
        public bool   ScamBlocked                     { get; set; }
        /// <summary>Human-readable explanation of why the token was scam-blocked.</summary>
        public string ScamReason                      { get; set; } = "";

        /// <summary>Full spike prediction (probability, live tape, execution plan) from the engine.</summary>
        public PredictionResult? Prediction           { get; set; }
    }
}
