using DexScannerBot.Core;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    /// <summary>
    /// Spike prediction engine — estimates the probability that a token spikes (reaches
    /// +WinReturnPct before falling SpikeStopPct, within SpikeHorizonMinutes, measured from
    /// the price a wallet would actually pay) and decides when to act.
    ///
    /// Pipeline:
    ///   1. Hard vetoes — confirmed fraud (honeypot, owner can change balances, ~100% tax,
    ///      already rugged, live freeze authority on Solana) blocks outright.
    ///   2. Feature vector — buy-flow, buy-count and volume bursts from DexScreener's
    ///      windows, plus live-tape readings (60-second buy-rate acceleration, slope,
    ///      curvature, breakout, liquidity change), an accumulation "coil" measure, late-
    ///      chase penalty, security / holder / wash / dump risk.
    ///   3. Probability — online logistic model (SpikeModel) that keeps learning from
    ///      every resolved outcome, so the probability scale self-calibrates.
    ///   4. Phase — accumulation → ignition → expansion → climax → distribution / fade.
    ///   5. Execution plan — price impact for the reference size on a constant-product pool,
    ///      latency buffer for a manual wallet entry, recommended slippage, max entry, stop,
    ///      targets, round-trip cost via an aggregator vs Phantom's in-wallet swap, and the
    ///      resulting expected edge.
    ///   6. Timing decision — INVEST_NOW / ARM / WAIT_FOR_PULLBACK / AVOID / SKIP with an
    ///      ETA. INVEST_NOW also requires a positive edge after costs.
    ///
    /// No model can know the future; this one is built to be honest about it: every call
    /// is tracked, the measured hit rate per probability bucket is reported on the
    /// dashboard, and the model is flagged "uncalibrated" until enough outcomes exist.
    /// </summary>
    public sealed class SpikePredictionEngine
    {
        private readonly PredictionConfig _cfg;
        private readonly SpikeModel _model;
        private readonly Func<int> _resolvedCount;

        public SpikePredictionEngine(PredictionConfig cfg, SpikeModel model, Func<int>? resolvedCount = null)
        {
            _cfg = cfg;
            _model = model;
            _resolvedCount = resolvedCount ?? (() => 0);
        }

        public SpikePredictionEngine() : this(new PredictionConfig(), new SpikeModel()) { }

        public SpikeModel Model => _model;

        public PredictionResult Predict(TokenAnalysis a, SecurityIntel? intel, RugDetectionResult? rug, TapeFeatures? live = null)
        {
            int resolved = _resolvedCount();
            bool calibrated = resolved >= _cfg.CalibrationMinSamples;
            var live2 = live != null && live.HasLive ? live : null;

            // ── 1. Hard vetoes ─────────────────────────────────────────────
            var veto = HardVeto(a, intel, rug);
            if (veto != null)
            {
                return new PredictionResult
                {
                    CalibratedScore = 0,
                    SpikeProbability = 0,
                    Confidence = 100,
                    Label = "SCAM_BLOCKED",
                    IsScamBlocked = true,
                    ScamReason = veto,
                    EntryDecision = "AVOID",
                    TimingSummary = $"Blocked: {veto}",
                    SpikeWindow = "never",
                    MomentumPhase = "blocked",
                    OutcomeSamples = resolved,
                    ModelCalibrated = calibrated,
                    Live = live,
                    Cautions = new List<string> { veto },
                    Verdicts = new List<SourceVerdict>
                    {
                        new() { Source = "security_veto", Signal = "sec_critical_veto", Vote = Vote.Bearish, PriorWeight = 10, LearnedReliability = 1, Detail = $"HARD FRAUD VETO: {veto}" }
                    },
                    Plan = BuildPlan(a, intel, live2, 0)
                };
            }

            // ── 2. Features ────────────────────────────────────────────────
            var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var x = BuildFeatures(a, intel, rug, live2, notes);

            // ── 3. Probability ─────────────────────────────────────────────
            double logit = _model.Logit(x, out var contrib);
            double p = SpikeModel.Sigmoid(logit);

            // ── 4. Phase ───────────────────────────────────────────────────
            string phase = ClassifyPhase(a, live2, x);

            // ── 5. Execution plan ──────────────────────────────────────────
            var plan = BuildPlan(a, intel, live2, p);

            // ── 6. Timing ──────────────────────────────────────────────────
            var cautions = new List<string>();
            var timing = DecideTiming(phase, p, plan, a, live2, x, rug, intel, cautions);

            // ── Confidence: how much evidence stands behind the number ─────
            double dataQ = 0.45
                + (live2 != null ? 0.22 : 0)
                + (intel != null ? 0.15 : 0)
                + (a.Buys5m + a.Sells5m >= 10 ? 0.08 : 0)
                + (calibrated ? 0.10 : 0);
            // Agreement: share of the strongest signals pointing the same way as the net evidence.
            double netEvidence = contrib.Values.Sum();
            var top = contrib.Values.OrderByDescending(v => Math.Abs(v)).Take(6).ToList();
            double agree = top.Count == 0 ? 0.5 : top.Count(c => Math.Sign(c) == Math.Sign(netEvidence)) / (double)top.Count;
            double confidence = Math.Round(100 * Clamp(dataQ * (0.65 + 0.35 * agree), 0, 1), 1);

            // ── Breakdown / verdicts for the dashboard ─────────────────────
            var breakdown = contrib
                .Where(kv => Math.Abs(kv.Value) >= 0.01)
                .OrderByDescending(kv => Math.Abs(kv.Value))
                .Select(kv => new SignalContribution
                {
                    Name = SpikeModel.DisplayNames.TryGetValue(kv.Key, out var dn) ? dn : kv.Key,
                    Delta = Math.Round(kv.Value, 3),
                    Positive = kv.Value > 0,
                    Note = notes.TryGetValue(kv.Key, out var n) ? n : ""
                })
                .ToList();

            var verdicts = breakdown.Where(b => Math.Abs(b.Delta) >= 0.15).Select(b => new SourceVerdict
            {
                Source = "spike_model",
                Signal = b.Name,
                Vote = b.Delta > 0 ? Vote.Bullish : Vote.Bearish,
                PriorWeight = Math.Abs(b.Delta),
                LearnedReliability = 0.5,
                Detail = string.IsNullOrEmpty(b.Note) ? b.Name : $"{b.Name}: {b.Note}"
            }).ToList();

            return new PredictionResult
            {
                CalibratedScore = Math.Round(p * 100, 1),
                SpikeProbability = Math.Round(p, 4),
                Confidence = confidence,
                Label = LabelFor(timing.Decision, p),
                Verdicts = verdicts,
                Breakdown = breakdown,
                OutcomeSamples = resolved,
                ModelCalibrated = calibrated,
                EntryDecision = timing.Decision,
                InvestInSeconds = Math.Round(timing.Seconds),
                SpikeWindow = timing.Window,
                TimingSummary = timing.Summary,
                SuggestedPullbackPct = Math.Round(timing.PullbackPct, 1),
                MomentumPhase = phase,
                Features = x,
                Live = live,
                Plan = plan,
                Cautions = cautions,
                PredictedAt = DateTime.UtcNow
            };
        }

        // ─────────────────────────────────────────────────────────────────
        //  1. Vetoes
        // ─────────────────────────────────────────────────────────────────

        private static string? HardVeto(TokenAnalysis a, SecurityIntel? intel, RugDetectionResult? rug)
        {
            if (intel != null)
            {
                if (intel.IsRugged) return "Already rugged (provider report)";
                if (intel.IsHoneypot) return "Confirmed honeypot — sells fail";
                if (intel.OwnerCanChangeBalance) return "Owner can alter wallet balances";
                if (intel.SellTaxPct >= 99) return "~100% sell tax";
                if (intel.FreezeAuthorityActive && a.Chain.Equals("solana", StringComparison.OrdinalIgnoreCase))
                    return "Freeze authority live — your tokens can be frozen in-wallet";
            }
            if (rug != null)
            {
                if (rug.Contract.IsHoneypot) return "Confirmed honeypot — sells fail";
                if (rug.Contract.OwnerCanChangeBalance) return "Owner can alter wallet balances";
                if (rug.RiskScore.TotalScore >= 81) return $"Critical rug risk ({rug.RiskScore.TotalScore}/100)";
                if (rug.RiskScore.ContractRisk >= 55) return $"Critical contract risk ({rug.RiskScore.ContractRisk}/100)";
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────
        //  2. Features
        // ─────────────────────────────────────────────────────────────────

        public static Dictionary<string, double> BuildFeatures(TokenAnalysis a, SecurityIntel? intel,
            RugDetectionResult? rug, TapeFeatures? live, Dictionary<string, string>? notes = null)
        {
            notes ??= new Dictionary<string, string>();
            var x = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Buy flow (shrunk toward neutral when there are too few trades to trust it)
            int n5 = a.Buys5m + a.Sells5m;
            double flow5 = Clamp((a.BuyPressure5m - 50) / 25.0, -2, 2) * Math.Min(1.0, n5 / 6.0);
            x[SpikeModel.Flow5m] = flow5;
            notes[SpikeModel.Flow5m] = $"{a.BuyPressure5m:F0}% buys over {n5} trades (5m)";

            int n1 = a.Buys1h + a.Sells1h;
            x[SpikeModel.Flow1h] = Clamp((a.BuyPressure1h - 50) / 25.0, -2, 2) * Math.Min(1.0, n1 / 20.0);
            notes[SpikeModel.Flow1h] = $"{a.BuyPressure1h:F0}% buys over {n1} trades (1h)";

            // Buy-count burst: buys/min in the last 5m vs the hourly average
            double br5 = a.Buys5m / 5.0, br1 = a.Buys1h / 60.0;
            double txBurstRatio = (br5 + 0.2) / (br1 + 0.2);
            x[SpikeModel.TxBurst] = Clamp(Math.Log(txBurstRatio), -1.5, 2.0);
            notes[SpikeModel.TxBurst] = $"{br5:F1} buys/min now vs {br1:F1}/min hourly ({txBurstRatio:F1}×)";

            // Volume burst: last 5m $ pace vs hourly
            if (a.Vol1h > 0)
            {
                double vb = (a.Vol5m * 12 + 1) / (a.Vol1h + 1);
                x[SpikeModel.VolBurst] = Clamp(Math.Log(vb), -1.5, 2.0);
                notes[SpikeModel.VolBurst] = $"5m $ pace {vb:F1}× the hourly pace";
            }
            else x[SpikeModel.VolBurst] = 0;

            x[SpikeModel.VolAccel] = a.VolumeAcceleration > 0 ? Clamp(Math.Log(a.VolumeAcceleration), -2, 2.5) : -1;
            notes[SpikeModel.VolAccel] = $"1h $ pace {a.VolumeAcceleration:F1}× the daily pace";

            // Live tape
            double liveAccelFeature = 0;
            if (live != null)
            {
                liveAccelFeature = Clamp(Math.Log((live.BuyRatePerMin + 0.3) / (live.BaselineBuyRatePerMin + 0.3)), -1.5, 2.0);
                x[SpikeModel.LiveBuyAccel] = liveAccelFeature;
                notes[SpikeModel.LiveBuyAccel] = $"{live.BuyRatePerMin:F1} buys/min last 60s vs {live.BaselineBuyRatePerMin:F1} hourly";

                x[SpikeModel.LiveFlow] = live.RecentTxns >= 4 ? Clamp((live.NetFlowPct - 50) / 25.0, -2, 2) : 0;
                notes[SpikeModel.LiveFlow] = $"{live.NetFlowPct:F0}% buys in last 60s ({live.RecentTxns} trades)";

                x[SpikeModel.LiveSlope] = Math.Tanh(live.PriceSlopePctPerMin / 3.0);
                notes[SpikeModel.LiveSlope] = $"{live.PriceSlopePctPerMin:+0.0;-0.0}%/min over 2m";

                x[SpikeModel.LiveCurve] = Math.Tanh((live.SlopeShortPctPerMin - live.SlopeLongPctPerMin) / 3.0);
                notes[SpikeModel.LiveCurve] = $"1m slope {live.SlopeShortPctPerMin:+0.0;-0.0} vs 4m {live.SlopeLongPctPerMin:+0.0;-0.0} %/min";

                x[SpikeModel.LiveBreakout] = live.Breakout ? 1 : 0;
                notes[SpikeModel.LiveBreakout] = live.Breakout ? "price just cleared its 5-minute high" : "inside 5-minute range";

                x[SpikeModel.LiveLiq] = Math.Tanh(live.LiqChangePct / 10.0);
                notes[SpikeModel.LiveLiq] = $"liquidity {live.LiqChangePct:+0.0;-0.0}% over 3m";
            }

            // Accumulation coil: strong buying + rising activity while price is still quiet
            double flowStrength = Math.Max(0, Math.Max(flow5, live != null ? x[SpikeModel.LiveFlow] : 0));
            double burst = Math.Max(0, Math.Max(x[SpikeModel.TxBurst], liveAccelFeature));
            double quietMove = live != null ? Math.Abs(live.PriceSlopePctPerMin) * 2 : Math.Abs(a.Change5m);
            double priceQuiet = 1 - Clamp(quietMove / 6.0, 0, 1);
            double coil = Clamp(flowStrength * burst * priceQuiet, 0, 1.5);
            x[SpikeModel.Coil] = coil;
            notes[SpikeModel.Coil] = coil > 0.05 ? "buyers stacking while price is still flat" : "no coiling";

            // Late chase: the move already happened
            double late = Math.Tanh(Math.Max(0, a.Change1h - 40) / 60.0) + Math.Tanh(Math.Max(0, a.Change5m - 15) / 20.0);
            x[SpikeModel.LateChase] = late;
            notes[SpikeModel.LateChase] = $"+{Math.Max(0, a.Change1h):F0}% 1h, {a.Change5m:+0;-0}% 5m already printed";

            // Market structure
            x[SpikeModel.LiqDepth] = Math.Tanh(Math.Log(Math.Max(a.LiquidityUsd, 1) / 15000.0));
            notes[SpikeModel.LiqDepth] = $"${a.LiquidityUsd:N0} pooled";
            double cap = a.MarketCap > 0 ? a.MarketCap : a.Fdv;
            x[SpikeModel.CapRoom] = cap > 0 ? Math.Tanh(Math.Log(2_000_000 / cap) / 2.0) : 0;
            notes[SpikeModel.CapRoom] = cap > 0 ? $"${cap:N0} cap" : "cap unknown";
            x[SpikeModel.AgeFresh] = a.AgeHours < 6 ? 1 : a.AgeHours < 24 ? 0.5 : a.AgeHours > 72 ? -0.5 : 0;
            notes[SpikeModel.AgeFresh] = a.AgeHours >= 9000 ? "age unknown" : $"{a.AgeHours:F1}h old";

            // Security
            x[SpikeModel.Security] = ScoreSecurity(intel, rug, out var secNote);
            notes[SpikeModel.Security] = secNote;

            double holder = ScoreHolderRisk(intel, out var holderNote);
            x[SpikeModel.HolderRisk] = holder;
            notes[SpikeModel.HolderRisk] = holderNote;

            double wash = ScoreWashRisk(a, rug, out var washNote);
            x[SpikeModel.WashRisk] = wash;
            notes[SpikeModel.WashRisk] = washNote;

            double dump = ScoreDumpRisk(a, rug, live, out var dumpNote);
            x[SpikeModel.DumpRisk] = dump;
            notes[SpikeModel.DumpRisk] = dumpNote;

            x[SpikeModel.Attention] = a.IsBoosted ? 1 : 0;
            notes[SpikeModel.Attention] = a.IsBoosted ? "active DexScreener boost" : "no boost";
            x[SpikeModel.Socials] = a.SocialPresence >= 2 ? 1 : a.SocialPresence == 1 ? 0.4 : -1;
            notes[SpikeModel.Socials] = $"{a.SocialPresence} web/social link(s)";

            foreach (var k in x.Keys.ToList())
                if (!double.IsFinite(x[k])) x[k] = 0;
            return x;
        }

        private static double ScoreSecurity(SecurityIntel? intel, RugDetectionResult? rug, out string note)
        {
            double s = 0;
            var parts = new List<string>();
            if (rug != null)
            {
                int t = rug.RiskScore.TotalScore;
                s += t <= 20 ? 0.6 : t <= 40 ? 0.2 : t <= 60 ? -0.4 : -1.0;
                parts.Add($"rug risk {t}/100");
            }
            if (intel != null)
            {
                if (intel.LpLockedPct >= 90) { s += 0.4; parts.Add($"LP {intel.LpLockedPct:F0}% locked/burned"); }
                else if (intel.LpLockedPct >= 0 && intel.LpLockedPct < 50) { s -= 0.5; parts.Add($"LP only {intel.LpLockedPct:F0}% locked"); }
                if (intel.MintAuthorityActive) { s -= 0.8; parts.Add("mint authority live"); }
                if (intel.FreezeAuthorityActive) { s -= 0.8; parts.Add("freeze authority live"); }
                if (intel.HasHiddenOwner) { s -= 0.4; parts.Add("hidden owner"); }
                if (intel.SellTaxPct > 10) { s -= 0.6; parts.Add($"sell tax {intel.SellTaxPct:F0}%"); }
                if (intel.DangerRiskCount > 0) { s -= 0.2 * Math.Min(3, intel.DangerRiskCount); parts.Add($"{intel.DangerRiskCount} danger flag(s)"); }
                if (parts.Count == (rug != null ? 1 : 0)) parts.Add($"clean {intel.Source} report");
            }
            else
            {
                s -= 0.1;
                parts.Add("no external security report");
            }
            note = string.Join(", ", parts);
            return Clamp(s, -2, 1.2);
        }

        private static double ScoreHolderRisk(SecurityIntel? intel, out string note)
        {
            if (intel == null) { note = "holder data unavailable"; return 0; }
            double r = 0;
            var parts = new List<string>();
            if (intel.Top10HolderPct >= 50) { r += 1.0; parts.Add($"top-10 hold {intel.Top10HolderPct:F0}%"); }
            else if (intel.Top10HolderPct >= 35) { r += 0.5; parts.Add($"top-10 hold {intel.Top10HolderPct:F0}%"); }
            else if (intel.Top10HolderPct >= 0) parts.Add($"top-10 hold {intel.Top10HolderPct:F0}%");
            if (intel.CreatorHolderPct >= 10) { r += 0.8; parts.Add($"creator holds {intel.CreatorHolderPct:F1}%"); }
            else if (intel.CreatorHolderPct >= 5) { r += 0.3; parts.Add($"creator holds {intel.CreatorHolderPct:F1}%"); }
            if (intel.InsiderNetworkCount > 0) { r += 0.5; parts.Add($"{intel.InsiderNetworkCount} insider network(s)"); }
            note = parts.Count > 0 ? string.Join(", ", parts) : "holder data unavailable";
            return Clamp(r, 0, 2);
        }

        private static double ScoreWashRisk(TokenAnalysis a, RugDetectionResult? rug, out string note)
        {
            double r = 0;
            var parts = new List<string>();
            if (a.VolumeToLiqRatio > 60) { r += 0.6; parts.Add($"24h volume {a.VolumeToLiqRatio:F0}× liquidity"); }
            int tx24 = a.Buys24h + a.Sells24h;
            if (a.Vol24h > 100_000 && tx24 < 15) { r += 1.0; parts.Add("big $ volume on almost no trades"); }
            if (rug?.Developer.IsWashTrading == true) { r += 0.8; parts.Add("balanced bot-like trade counts"); }
            int tx1 = a.Buys1h + a.Sells1h;
            if (tx1 > 5 && a.LiquidityUsd > 0 && a.Vol1h / tx1 > a.LiquidityUsd * 0.05)
            { r += 0.4; parts.Add("average trade > 5% of the pool"); }
            note = parts.Count > 0 ? string.Join(", ", parts) : "volume looks organic";
            return Clamp(r, 0, 2);
        }

        private static double ScoreDumpRisk(TokenAnalysis a, RugDetectionResult? rug, TapeFeatures? live, out string note)
        {
            double r = 0;
            var parts = new List<string>();
            if (rug?.Developer.HasCoordinatedDump == true) { r += 1.0; parts.Add("5m sells ≥3× buys"); }
            if (rug?.Liquidity.IsLiquidityDraining == true) { r += 1.0; parts.Add($"liquidity pulled {rug.Liquidity.LiquidityDrainPct:F0}%"); }
            if (live != null)
            {
                if (live.RecentTxns >= 6 && live.SellRatePerMin > 2 * Math.Max(0.5, live.BuyRatePerMin)) { r += 0.8; parts.Add("sellers 2× buyers in last 60s"); }
                if (live.DrawFromHighPct > 15) { r += 0.5; parts.Add($"{live.DrawFromHighPct:F0}% off the 5m high"); }
                if (live.LiqChangePct < -10) { r += 0.6; parts.Add($"liquidity {live.LiqChangePct:F0}% in 3m"); }
            }
            note = parts.Count > 0 ? string.Join(", ", parts) : "no dump pressure";
            return Clamp(r, 0, 2);
        }

        // ─────────────────────────────────────────────────────────────────
        //  4. Phase
        // ─────────────────────────────────────────────────────────────────

        public static string ClassifyPhase(TokenAnalysis a, TapeFeatures? live, IReadOnlyDictionary<string, double> x)
        {
            double slope = live?.PriceSlopePctPerMin ?? a.Change5m / 5.0;
            double txBurst = Math.Exp(x.TryGetValue(SpikeModel.TxBurst, out var tb) ? tb : 0);
            double coil = x.TryGetValue(SpikeModel.Coil, out var c) ? c : 0;

            if (a.Change1h > 60 && (a.BuyPressure5m < 50 || (live != null && slope < 0)))
                return "distribution";

            if ((a.Change5m < -4 && a.Change1h < -8) || (live != null && live.PriceSlopePctPerMin < -1.5 && live.NetFlowPct < 50))
                return "fade";

            if (a.Change5m > 25 || (a.Change1h > 45 && a.VolumeAcceleration >= 4 && (a.BuyPressure1h < 55 || a.BuyPressure5m < 60)))
                return "climax";

            bool liveIgnite = live != null && live.Breakout && live.BuyRateAccel >= 1.8 && live.PriceSlopePctPerMin > 0.4;
            bool snapIgnite = txBurst >= 2.0 && a.Change5m >= 2 && a.Change5m <= 25 && a.BuyPressure5m >= 60;
            if (liveIgnite || snapIgnite)
                return "ignition";

            if (coil >= 0.25 && a.BuyPressure5m >= 56)
                return "accumulation";

            if (a.Change5m > 0 && a.Change1h > 0 && a.VolumeAcceleration >= 1.5)
                return "expansion";

            if (a.Change5m <= 0 && a.Change1h <= 0)
                return "cooling";

            return "neutral";
        }

        // ─────────────────────────────────────────────────────────────────
        //  5. Execution plan
        // ─────────────────────────────────────────────────────────────────

        public ExecutionPlan BuildPlan(TokenAnalysis a, SecurityIntel? intel, TapeFeatures? live, double p)
        {
            double price = DexJson.ParsePrice(a.PriceUsd);
            double size = Math.Max(1, _cfg.ReferenceTradeUsd);
            // Constant-product pool: the quote side holds ≈ half the USD liquidity, and the
            // average execution price of a buy of size s is (Q + s)/B vs spot Q/B → impact s/Q.
            double quoteReserve = Math.Max(a.LiquidityUsd / 2.0, 1);
            double impact = Math.Min(100.0, 100.0 * size / quoteReserve);

            double dexFee = DexFeePct(a.Dex, a.Chain);
            double buyTax = Math.Max(0, intel?.BuyTaxPct ?? 0);
            double sellTax = Math.Max(0, intel?.SellTaxPct ?? 0);

            // Adverse move expected while a human confirms the swap in a wallet (~20s).
            double latencyBuffer;
            if (live != null && live.TickVolatilityPct > 0 && live.TickSeconds > 0)
                latencyBuffer = live.TickVolatilityPct * Math.Sqrt(20.0 / live.TickSeconds) + Math.Max(0, live.PriceSlopePctPerMin) / 3.0;
            else
                latencyBuffer = Math.Abs(a.Change5m) / Math.Sqrt(15.0) + 0.3;
            latencyBuffer = Clamp(latencyBuffer, 0.2, 15);

            double slippage = Clamp(impact * 1.25 + latencyBuffer * 1.5 + buyTax + 0.3, 0.5, 30);
            slippage = Math.Round(slippage, 1);

            double chase = Clamp(Math.Max(2.0, latencyBuffer * 2), 2, 8);
            double stopPct = Clamp(Math.Max(_cfg.SpikeStopPct, latencyBuffer * 4), _cfg.SpikeStopPct, 30);
            double tp1 = _cfg.WinReturnPct;
            double tp2 = _cfg.WinReturnPct * 2.4;

            double roundTrip = 2 * impact + 2 * dexFee + buyTax + sellTax;
            double roundTripPhantom = roundTrip + 2 * _cfg.PhantomSwapFeePct;
            double ev = p * tp1 - (1 - p) * stopPct - roundTrip;
            double evPhantom = p * tp1 - (1 - p) * stopPct - roundTripPhantom;

            bool solana = a.Chain.Equals("solana", StringComparison.OrdinalIgnoreCase);
            string route = solana ? "Jupiter"
                : a.Chain.Equals("bsc", StringComparison.OrdinalIgnoreCase) ? "PancakeSwap"
                : "Uniswap";
            string walletNote = solana
                ? $"Phantom / Jupiter: set slippage to {slippage:F1}% ({(int)Math.Round(slippage * 100)} bps) instead of Auto. Keep ~0.01 SOL for fees + priority fee."
                : $"Phantom / MetaMask on {a.Chain}: set slippage to {slippage:F1}%; the first buy needs a token approval and native gas.";
            if (impact > 2)
                walletNote += $" Pool is thin: ${quoteReserve * 0.02:N0} keeps impact under 2%.";

            return new ExecutionPlan
            {
                PriceUsd = price,
                ReferenceSizeUsd = size,
                PriceImpactPct = Math.Round(impact, 2),
                LatencyBufferPct = Math.Round(latencyBuffer, 2),
                RecommendedSlippagePct = slippage,
                RecommendedSlippageBps = (int)Math.Round(slippage * 100),
                MaxEntryPrice = price * (1 + chase / 100),
                StopLossPct = Math.Round(stopPct, 1),
                StopLossPrice = price * (1 + impact / 100) * (1 - stopPct / 100),
                TakeProfit1Pct = tp1,
                TakeProfit1Price = price * (1 + impact / 100) * (1 + tp1 / 100),
                TakeProfit2Pct = Math.Round(tp2, 1),
                TakeProfit2Price = price * (1 + impact / 100) * (1 + tp2 / 100),
                DexFeePct = dexFee,
                BuyTaxPct = Math.Round(buyTax, 2),
                SellTaxPct = Math.Round(sellTax, 2),
                RoundTripCostPct = Math.Round(roundTrip, 2),
                RoundTripCostPhantomPct = Math.Round(roundTripPhantom, 2),
                ExpectedValuePct = Math.Round(ev, 2),
                ExpectedValuePhantomPct = Math.Round(evPhantom, 2),
                MaxSizeUsdFor2PctImpact = Math.Round(quoteReserve * 0.02, 0),
                Route = route,
                WalletNote = walletNote
            };
        }

        public static double DexFeePct(string? dex, string? chain)
        {
            var d = (dex ?? "").ToLowerInvariant();
            if (d == "pumpfun") return 1.0;          // bonding-curve trade fee
            if (d.Contains("pumpswap")) return 0.25;
            if (d.Contains("raydium")) return 0.25;
            if (d.Contains("orca")) return 0.30;
            if (d.Contains("meteora")) return 0.30;  // dynamic; typical
            if (d.Contains("uniswap")) return 0.30;
            if (d.Contains("aerodrome")) return 0.30;
            if (d.Contains("pancake")) return 0.25;
            return 0.30;
        }

        // ─────────────────────────────────────────────────────────────────
        //  6. Timing
        // ─────────────────────────────────────────────────────────────────

        private (string Decision, double Seconds, double PullbackPct, string Window, string Summary) DecideTiming(
            string phase, double p, ExecutionPlan plan, TokenAnalysis a, TapeFeatures? live,
            IReadOnlyDictionary<string, double> x, RugDetectionResult? rug, SecurityIntel? intel, List<string> cautions)
        {
            double T = _cfg.InvestThreshold;
            double late = x.TryGetValue(SpikeModel.LateChase, out var l) ? l : 0;
            double coil = x.TryGetValue(SpikeModel.Coil, out var c) ? c : 0;
            double dump = x.TryGetValue(SpikeModel.DumpRisk, out var d) ? d : 0;

            // Soft blockers that make any entry a bad trade for a wallet user.
            if (rug?.Liquidity.IsLiquidityDraining == true || (live != null && live.LiqChangePct < -20))
            {
                cautions.Add("Liquidity is being pulled.");
                return ("AVOID", 0, 0, "never", "Liquidity is being removed from the pool — classic rug sequence.");
            }
            if (intel != null && intel.SellTaxPct > 15)
            {
                cautions.Add($"Sell tax {intel.SellTaxPct:F0}% eats any spike.");
                return ("AVOID", 0, 0, "never", $"Sell tax of {intel.SellTaxPct:F0}% makes a profitable exit unlikely.");
            }
            if (dump >= 1.2)
            {
                cautions.Add("Active dump pressure.");
                return ("AVOID", 0, 0, "never", "Sellers are in control right now (dump / drain signals).");
            }

            (string Decision, double Seconds, double PullbackPct, string Window, string Summary) r;
            switch (phase)
            {
                case "distribution":
                    r = ("AVOID", 0, 0, "never", "Distribution: early holders are selling into strength — late buyers become exit liquidity.");
                    break;

                case "fade":
                    r = ("AVOID", 0, 0, "never", "Momentum is fading; buying here fights the trend.");
                    break;

                case "cooling":
                    r = ("SKIP", 0, 0, "—", "Price and flow are cooling — no spike setup forming.");
                    break;

                case "climax":
                {
                    double wait = EstimatedPullbackSeconds(a);
                    double pb = SuggestedPullback(a);
                    r = ("WAIT_FOR_PULLBACK", wait, pb, $"~{wait:F0}s",
                        $"Vertical move already printed ({a.Change5m:+0;-0}% 5m, {a.Change1h:+0;-0}% 1h). Wait ~{wait:F0}s for a {pb:F0}% pullback instead of buying the top.");
                    break;
                }

                case "ignition":
                    if (p >= T)
                        r = ("INVEST_NOW", 0, 0, "now",
                            live != null
                                ? $"Ignition: buys {live.BuyRateAccel:F1}× their hourly pace and price is breaking out. Spike window is open now."
                                : "Ignition: buy-count burst with price turning up. Spike window is open now.");
                    else if (p >= T - 0.15)
                        r = ("ARM", 30, 0, "~30s", $"Ignition pattern but probability {p * 100:F0}% is under the {T * 100:F0}% bar — confirm on the next ticks.");
                    else
                        r = ("SKIP", 0, 0, "—", $"Breakout attempt with weak backing ({p * 100:F0}%).");
                    break;

                case "accumulation":
                {
                    double eta = Clamp(300 - 240 * Clamp(coil, 0, 1) - (live?.BuyRateAccel >= 1.5 ? 60 : 0), 20, 300);
                    bool primed = live != null && live.BuyRateAccel >= 1.5 && live.SlopeShortPctPerMin >= 0;
                    if (p >= T + 0.08 && primed)
                        r = ("INVEST_NOW", 0, 0, "0–2 min",
                            $"Accumulation is tipping over: buyers stacking ({live!.BuyRateAccel:F1}× hourly pace) with price still near the base. Early entry before the breakout.");
                    else if (p >= T - 0.15)
                        r = ("ARM", eta, 0, $"~{FormatEta(eta)}",
                            $"Buyers are stacking while price is flat — spike likely to start in ~{FormatEta(eta)} (estimate). Get the swap ready; enter on the breakout.");
                    else
                        r = ("SKIP", 0, 0, "—", $"Mild accumulation, probability {p * 100:F0}% too low.");
                    break;
                }

                case "expansion":
                    if (late > 0.3)
                    {
                        double pb = SuggestedPullback(a);
                        r = ("WAIT_FOR_PULLBACK", EstimatedPullbackSeconds(a), pb, "after pullback",
                            $"Trend is up but extended ({a.Change1h:+0;-0}% 1h). Wait for a {pb:F0}% dip.");
                    }
                    else if (p >= T)
                        r = ("INVEST_NOW", 0, 0, "now", "Trend continuation with buyers in control.");
                    else if (p >= T - 0.15)
                        r = ("ARM", 60, 0, "~1 min", $"Trend up, probability {p * 100:F0}% — wait for a fresh burst of buys.");
                    else
                        r = ("SKIP", 0, 0, "—", $"Grinding up without a spike signature ({p * 100:F0}%).");
                    break;

                default:
                    r = p >= T
                        ? ("ARM", 90, 0, "~1–2 min", "Strong underlying setup but no trigger yet — watch for a breakout.")
                        : ("SKIP", 0, 0, "—", "No clear spike setup.");
                    break;
            }

            // Wallet reality checks on any buy call.
            if (r.Decision == "INVEST_NOW")
            {
                if (plan.ExpectedValuePct <= 0)
                {
                    cautions.Add($"Costs ({plan.RoundTripCostPct:F1}% round trip) exceed the edge at ${plan.ReferenceSizeUsd:N0}.");
                    r = ("ARM", 30, 0, "~30s", r.Summary + $" Downgraded: round-trip cost {plan.RoundTripCostPct:F1}% leaves no edge at this size.");
                }
                else if (plan.PriceImpactPct > 8)
                {
                    cautions.Add($"Pool too thin: {plan.PriceImpactPct:F1}% impact for ${plan.ReferenceSizeUsd:N0}.");
                    r = ("ARM", 30, 0, "~30s", r.Summary + $" Downgraded: size down to ≤${plan.MaxSizeUsdFor2PctImpact:N0} to keep impact under 2%.");
                }
                else if (plan.ExpectedValuePhantomPct <= 0)
                {
                    cautions.Add("Use Jupiter/aggregator routing — Phantom's swap fee removes the edge on this one.");
                }
            }
            return r;
        }

        private static string FormatEta(double s) => s >= 90 ? $"{s / 60:F0} min" : $"{s:F0}s";

        private static double SuggestedPullback(TokenAnalysis a)
            => Math.Min(Math.Max(3, Math.Abs(a.Change1h) * 0.15), 15);

        private static double EstimatedPullbackSeconds(TokenAnalysis a)
        {
            double volFade = Math.Max(0.2, Math.Min(2.0, 60 / Math.Max(15, a.TxnVelocity1h)));
            return Clamp(60 * volFade, 30, 180);
        }

        private static string LabelFor(string decision, double p) => decision switch
        {
            "INVEST_NOW" => p >= 0.75 ? "SPIKE IMMINENT" : "BUY WINDOW OPEN",
            "ARM" => "ARMED — SPIKE BUILDING",
            "WAIT_FOR_PULLBACK" => "LATE — WAIT FOR PULLBACK",
            "AVOID" => "AVOID",
            _ => "NO SETUP"
        };

        private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));
    }
}
