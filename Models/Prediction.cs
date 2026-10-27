namespace DexScannerBot.Models
{
    public enum Vote { Bullish, Bearish, Neutral }

    /// <summary>
    /// One signal's verdict on a token, derived from a feature's contribution to the
    /// spike model (kept for the UI and for backward-compatible payloads).
    /// </summary>
    public class SourceVerdict
    {
        public string Source { get; set; } = "";
        public string Signal { get; set; } = "";
        public Vote Vote { get; set; } = Vote.Neutral;
        public double PriorWeight { get; set; }
        public double LearnedReliability { get; set; } = 0.5;
        public string Detail { get; set; } = "";

        public double EffectiveWeight => Math.Max(0.02, PriorWeight * (0.4 + 1.2 * LearnedReliability));
    }

    /// <summary>Full output of the spike prediction engine for one token at one moment.</summary>
    public class PredictionResult
    {
        /// <summary>Spike probability × 100 (0–100). Kept under this name for compatibility.</summary>
        public double CalibratedScore { get; set; }
        /// <summary>0–100: how much data backed this call (live tape, security intel, calibration, signal agreement).</summary>
        public double Confidence { get; set; }
        public string Label { get; set; } = "";
        public List<SourceVerdict> Verdicts { get; set; } = new();
        public int OutcomeSamples { get; set; }

        /// <summary>True when confirmed fraud forced a hard block — no amount of volume overrides it.</summary>
        public bool IsScamBlocked { get; set; }
        public string ScamReason { get; set; } = "";

        // ── Timing ─────────────────────────────────────────────────────────
        /// <summary>INVEST_NOW / ARM / WAIT_FOR_PULLBACK / AVOID / SKIP.</summary>
        public string EntryDecision { get; set; } = "SKIP";
        /// <summary>Estimated seconds until the spike window opens. 0 = it is open now.</summary>
        public double InvestInSeconds { get; set; }
        public string TimingSummary { get; set; } = "";
        /// <summary>Pullback (% below current) to wait for when the move is already extended.</summary>
        public double SuggestedPullbackPct { get; set; }
        /// <summary>accumulation / ignition / expansion / climax / distribution / fade / cooling / neutral.</summary>
        public string MomentumPhase { get; set; } = "unknown";
        /// <summary>Per-feature log-odds contribution to the spike probability.</summary>
        public List<SignalContribution> Breakdown { get; set; } = new();

        // ── Spike model ────────────────────────────────────────────────────
        /// <summary>Probability (0–1) that price reaches the spike target before the stop, within the horizon.</summary>
        public double SpikeProbability { get; set; }
        /// <summary>Human label for the expected window, e.g. "now", "~45s", "after pullback".</summary>
        public string SpikeWindow { get; set; } = "";
        /// <summary>True once enough real outcomes have been resolved to trust the probability scale.</summary>
        public bool ModelCalibrated { get; set; }
        /// <summary>Feature vector the probability was computed from (stored for learning).</summary>
        public Dictionary<string, double> Features { get; set; } = new();
        /// <summary>Live tape readings (null when the token has not been polled enough yet).</summary>
        public TapeFeatures? Live { get; set; }
        /// <summary>Wallet-ready execution plan (slippage, max entry, stop, targets, costs, edge).</summary>
        public ExecutionPlan Plan { get; set; } = new();
        /// <summary>Reasons the engine refuses or downgrades an entry.</summary>
        public List<string> Cautions { get; set; } = new();
        public DateTime PredictedAt { get; set; } = DateTime.UtcNow;

        public List<string> DisplayLines()
        {
            var lines = new List<string>();
            if (IsScamBlocked)
            {
                lines.Add($"🚫 SCAM BLOCKED: {ScamReason}");
                return lines;
            }
            lines.Add($"◆ {EntryDecision} · spike {SpikeProbability * 100:F0}% · {SpikeWindow} — {TimingSummary}");
            foreach (var c in Breakdown.OrderByDescending(c => Math.Abs(c.Delta)).Take(6))
                lines.Add($"{(c.Delta >= 0 ? "📈" : "📉")} {c.Name} {c.Delta:+0.00;-0.00} {c.Note}");
            return lines;
        }
    }

    /// <summary>One named factor contributing to the spike probability (log-odds units).</summary>
    public class SignalContribution
    {
        public string Name { get; set; } = "";
        public double Delta { get; set; }
        public bool Positive { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// What a wallet user (Phantom, Jupiter, Uniswap, …) needs to actually execute the call:
    /// realistic price impact for the reference size, the slippage setting to use, the
    /// highest price worth paying, stop/targets, round-trip costs and the expected edge.
    /// </summary>
    public class ExecutionPlan
    {
        public double PriceUsd { get; set; }
        public double ReferenceSizeUsd { get; set; }
        /// <summary>Average-price impact of a buy of ReferenceSizeUsd on a constant-product pool, %.</summary>
        public double PriceImpactPct { get; set; }
        /// <summary>Expected adverse move during a manual wallet entry (~20s), %.</summary>
        public double LatencyBufferPct { get; set; }
        public double RecommendedSlippagePct { get; set; }
        public int RecommendedSlippageBps { get; set; }
        /// <summary>Do not buy above this price — beyond it the edge is gone.</summary>
        public double MaxEntryPrice { get; set; }
        public double StopLossPrice { get; set; }
        public double StopLossPct { get; set; }
        public double TakeProfit1Price { get; set; }
        public double TakeProfit1Pct { get; set; }
        public double TakeProfit2Price { get; set; }
        public double TakeProfit2Pct { get; set; }
        public double DexFeePct { get; set; }
        public double BuyTaxPct { get; set; }
        public double SellTaxPct { get; set; }
        /// <summary>Round trip via an aggregator (Jupiter / Uniswap): impact both ways + pool fees + taxes, %.</summary>
        public double RoundTripCostPct { get; set; }
        /// <summary>Same, but swapping inside the Phantom wallet (adds Phantom's swap fee each way), %.</summary>
        public double RoundTripCostPhantomPct { get; set; }
        /// <summary>p·TP1 − (1−p)·SL − costs, via aggregator, %.</summary>
        public double ExpectedValuePct { get; set; }
        public double ExpectedValuePhantomPct { get; set; }
        /// <summary>Largest buy that keeps average price impact ≤ 2%.</summary>
        public double MaxSizeUsdFor2PctImpact { get; set; }
        public string Route { get; set; } = "";
        public string WalletNote { get; set; } = "";
    }

    /// <summary>
    /// Readings from the live market tape — successive DexScreener snapshots of one token
    /// taken every few seconds — which reveal short-window acceleration that the fixed
    /// 5m/1h/6h/24h aggregates hide.
    /// </summary>
    public class TapeFeatures
    {
        public bool HasLive { get; set; }
        public int Samples { get; set; }
        public double SpanSeconds { get; set; }
        public double TickSeconds { get; set; }
        /// <summary>Buys per minute over the most recent ~60s.</summary>
        public double BuyRatePerMin { get; set; }
        public double SellRatePerMin { get; set; }
        /// <summary>Average buys per minute over the past hour.</summary>
        public double BaselineBuyRatePerMin { get; set; }
        /// <summary>(recent buy rate + 0.3) / (baseline + 0.3). Above ~2 is a burst.</summary>
        public double BuyRateAccel { get; set; }
        /// <summary>Buys share of recent transactions, %.</summary>
        public double NetFlowPct { get; set; } = 50;
        public int RecentTxns { get; set; }
        public double VolumeRatePerMin { get; set; }
        public double VolumeBurst { get; set; }
        /// <summary>Price trend over ~2 min, % per minute (log-regression).</summary>
        public double PriceSlopePctPerMin { get; set; }
        public double SlopeShortPctPerMin { get; set; }
        public double SlopeLongPctPerMin { get; set; }
        /// <summary>High-low range over the last 3 minutes, %.</summary>
        public double RangePct { get; set; }
        /// <summary>Latest price broke above the prior 5-minute high.</summary>
        public bool Breakout { get; set; }
        public double LiqChangePct { get; set; }
        public double DrawFromHighPct { get; set; }
        /// <summary>Standard deviation of tick-to-tick returns, %.</summary>
        public double TickVolatilityPct { get; set; }
        /// <summary>Recent prices (oldest → newest) for sparklines.</summary>
        public List<double> Spark { get; set; } = new();
    }

    /// <summary>A prediction being followed to measure its real-world outcome.</summary>
    public class TrackedOutcome
    {
        public string Address { get; set; } = "";
        public string PairAddress { get; set; } = "";
        public string Chain { get; set; } = "";
        public string Symbol { get; set; } = "";
        /// <summary>Observed price when the prediction was made.</summary>
        public double EntryPrice { get; set; }
        /// <summary>Entry after estimated price impact — what a wallet would actually pay.</summary>
        public double EffectiveEntry { get; set; }
        public double Probability { get; set; }
        public string Decision { get; set; } = "";
        public string Phase { get; set; } = "";
        public double ScoreAtEntry { get; set; }
        public DateTime SurfacedAtUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public Dictionary<string, double> Features { get; set; } = new();
        public double MaxReturnPct { get; set; }
        public double MinReturnPct { get; set; }
        public double LastReturnPct { get; set; }
        public bool Resolved { get; set; }
        /// <summary>1 = spike (target hit before stop), 0 = no spike.</summary>
        public int Label { get; set; }
        public string Reason { get; set; } = "";
        public double LiquidityAtEntry { get; set; }
        public DateTime? ResolvedAtUtc { get; set; }
    }

    public class CalibrationBucket
    {
        public double Lo { get; set; }
        public double Hi { get; set; }
        public int Count { get; set; }
        public int Hits { get; set; }
        public double HitRate => Count > 0 ? (double)Hits / Count : 0;
    }

    public class ResolvedSummary
    {
        public string Symbol { get; set; } = "";
        public string Chain { get; set; } = "";
        public string Address { get; set; } = "";
        public double Probability { get; set; }
        public string Decision { get; set; } = "";
        public int Label { get; set; }
        public double MaxReturnPct { get; set; }
        public double MinReturnPct { get; set; }
        public string Reason { get; set; } = "";
        public DateTime ResolvedAtUtc { get; set; }
    }

    /// <summary>Persisted learning state (data/prediction_state.json).</summary>
    public class PredictionState
    {
        public int Version { get; set; } = 2;
        public int TotalResolved { get; set; }
        public int TotalWins { get; set; }
        public int ActionableResolved { get; set; }
        public int ActionableWins { get; set; }
        public double BrierSum { get; set; }
        public Dictionary<string, double> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public double Bias { get; set; }
        public bool HasModel { get; set; }
        public int ModelUpdates { get; set; }
        public int[] BucketCount { get; set; } = new int[10];
        public int[] BucketHits { get; set; } = new int[10];
        public List<ResolvedSummary> Recent { get; set; } = new();
    }

    public class PredictionConfig
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Spike target: price must rise this % above the effective entry.</summary>
        public double WinReturnPct { get; set; } = 25.0;
        /// <summary>Legacy loss threshold (kept for config compatibility).</summary>
        public double LossReturnPct { get; set; } = -30.0;
        /// <summary>Stop: if price falls this % below effective entry first, the call failed.</summary>
        public double SpikeStopPct { get; set; } = 15.0;
        /// <summary>The spike must happen within this many minutes.</summary>
        public int SpikeHorizonMinutes { get; set; } = 30;
        public int MinSamplesForLearning { get; set; } = 8;
        /// <summary>Resolved outcomes required before the probability scale is considered calibrated.</summary>
        public int CalibrationMinSamples { get; set; } = 30;
        public string StatePath { get; set; } = "data/prediction_state.json";
        public bool ProbeWebPresence { get; set; } = true;
        /// <summary>Reference buy size used for price-impact and slippage maths, USD.</summary>
        public double ReferenceTradeUsd { get; set; } = 100.0;
        /// <summary>Phantom's in-wallet swap fee, % per swap.</summary>
        public double PhantomSwapFeePct { get; set; } = 0.85;
        /// <summary>Minimum spike probability for an INVEST_NOW call.</summary>
        public double InvestThreshold { get; set; } = 0.55;
        /// <summary>Seconds between live-tape polls of watched tokens.</summary>
        public int TapePollSeconds { get; set; } = 8;
        /// <summary>Maximum tokens on the live watchlist.</summary>
        public int MaxWatched { get; set; } = 120;
    }
}
