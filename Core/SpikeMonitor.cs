using System.Collections.Concurrent;
using DexScannerBot.Models;
using DexScannerBot.Services;

namespace DexScannerBot.Core
{
    /// <summary>Copies rug and prediction results onto a TokenAnalysis (shared by scanner and monitor).</summary>
    public static class PredictionStamp
    {
        public static void StampRug(TokenAnalysis a, RugDetectionResult rug)
        {
            a.VerifiedReputable = rug.VerifiedReputable;
            a.Verification = rug.Verification;
            a.RugRiskScore = rug.RiskScore.TotalScore;
        }

        public static void Apply(TokenAnalysis a, PredictionResult p)
        {
            a.Prediction = p;
            a.CalibratedScore = p.CalibratedScore;
            a.PredictionConfidence = p.Confidence;
            a.PredictionLabel = p.Label;
            a.ScamBlocked = p.IsScamBlocked;
            a.ScamReason = p.ScamReason;
            a.EntryDecision = p.EntryDecision;
            a.InvestInSeconds = p.InvestInSeconds;
            a.TimingSummary = p.TimingSummary;
            a.SuggestedPullbackPct = p.SuggestedPullbackPct;
            a.MomentumPhase = p.MomentumPhase;
            a.Breakdown = p.Breakdown;
            a.PredictionVerdicts = p.Verdicts;
        }
    }

    /// <summary>
    /// Keeps watching tokens after they are first surfaced. On every live-tape tick it
    /// re-runs the analyzer (without entry gates), the rug detector and the spike engine,
    /// pushes updates to the dashboard when something meaningful changed, and fires a
    /// spike alert the moment a token's decision flips to INVEST_NOW.
    /// </summary>
    public sealed class SpikeMonitor
    {
        private sealed class Ctx
        {
            public SecurityIntel? Intel;
            public DateTime IntelAt;
            public bool IntelRefreshing;
            public string LastDecision = "";
            public double LastProbability;
            public DateTime LastBroadcast;
            public DateTime LastAlert = DateTime.MinValue;
            public int AvoidStreak;
        }

        private readonly MarketTape _tape;
        private readonly SpikePredictionEngine _engine;
        private readonly AnalysisEngine _analyzer;
        private readonly RugDetectService _rug = new();
        private readonly SecurityIntelService _intel;
        private readonly OutcomeTracker? _outcomes;
        private readonly ConcurrentDictionary<string, Ctx> _ctx = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Meaningful change on a tracked token (decision flip, ≥5 pt probability move, or 30s heartbeat).</summary>
        public event Action<TokenAnalysis, PredictionResult, RugDetectionResult, SecurityIntel?>? OnUpdate;
        /// <summary>A tracked token just turned INVEST_NOW.</summary>
        public event Action<TokenAnalysis, PredictionResult>? OnSpikeAlert;

        public int WatchingCount => _tape.WatchedCount;
        public MarketTape Tape => _tape;

        public SpikeMonitor(MarketTape tape, SpikePredictionEngine engine, AnalysisEngine analyzer,
            SecurityIntelService intel, OutcomeTracker? outcomes)
        {
            _tape = tape;
            _engine = engine;
            _analyzer = analyzer;
            _intel = intel;
            _outcomes = outcomes;
            _tape.OnTick += HandleTick;
        }

        /// <summary>Records a market snapshot (called for every scanned pair).</summary>
        public void Ingest(DexPair pair) => _tape.Ingest(pair);

        public TapeFeatures? GetLive(string chain, string address) => _tape.GetFeatures(chain, address);

        /// <summary>Starts (or refreshes) live tracking of a surfaced token.</summary>
        public void Track(TokenAnalysis a, SecurityIntel? intel, PredictionResult p)
        {
            string key = MarketTape.KeyOf(a.Chain, a.Address);
            var ctx = _ctx.GetOrAdd(key, _ => new Ctx());
            if (intel != null) { ctx.Intel = intel; ctx.IntelAt = DateTime.UtcNow; }
            ctx.LastDecision = p.EntryDecision;
            ctx.LastProbability = p.SpikeProbability;
            ctx.LastBroadcast = DateTime.UtcNow;

            if (p.IsScamBlocked) { _tape.Unwatch(a.Chain, a.Address); return; }

            // Watch longer the closer the token is to a spike.
            TimeSpan ttl = p.EntryDecision switch
            {
                "INVEST_NOW" => TimeSpan.FromMinutes(30),
                "ARM" => TimeSpan.FromMinutes(20),
                "WAIT_FOR_PULLBACK" => TimeSpan.FromMinutes(15),
                _ => p.SpikeProbability >= 0.25 ? TimeSpan.FromMinutes(8) : TimeSpan.Zero
            };
            if (ttl > TimeSpan.Zero)
                _tape.Watch(a.Chain, a.Address, a.Symbol, a.PairAddress, ttl);
        }

        private void HandleTick(TokenTape tape, DexPair pair)
        {
            if (!_ctx.TryGetValue(tape.Key, out var ctx)) return;

            var a = _analyzer.Analyze(pair, null, out _, applyGates: false);
            if (a == null) return;

            RefreshIntelIfStale(tape, ctx);
            var rug = _rug.Analyze(pair, a, ctx.Intel);
            PredictionStamp.StampRug(a, rug);

            var live = MarketTape.Compute(tape);
            var p = _engine.Predict(a, ctx.Intel, rug, live);
            PredictionStamp.Apply(a, p);

            var now = DateTime.UtcNow;
            bool decisionChanged = p.EntryDecision != ctx.LastDecision;
            bool moved = Math.Abs(p.SpikeProbability - ctx.LastProbability) >= 0.05;
            bool heartbeat = (now - ctx.LastBroadcast).TotalSeconds >= 30;
            bool alert = p.EntryDecision == "INVEST_NOW"
                && (ctx.LastDecision != "INVEST_NOW" || (now - ctx.LastAlert).TotalMinutes >= 3)
                && (now - ctx.LastAlert).TotalSeconds >= 90;

            if (p.IsScamBlocked || p.EntryDecision == "AVOID") ctx.AvoidStreak++;
            else ctx.AvoidStreak = 0;

            ctx.LastDecision = p.EntryDecision;
            ctx.LastProbability = p.SpikeProbability;

            // Learn from fresh calls too (the tracker ignores tokens it is already following).
            if (decisionChanged && (p.EntryDecision == "INVEST_NOW" || p.EntryDecision == "ARM"))
                _outcomes?.Record(a, p);

            if (decisionChanged || moved || heartbeat || alert)
            {
                ctx.LastBroadcast = now;
                try { OnUpdate?.Invoke(a, p, rug, ctx.Intel); } catch { }
            }
            if (alert)
            {
                ctx.LastAlert = now;
                try { OnSpikeAlert?.Invoke(a, p); } catch { }
            }

            // Keep watching while the setup is alive; drop it after repeated AVOIDs.
            if (p.IsScamBlocked || ctx.AvoidStreak >= 3)
                _tape.Unwatch(tape.Chain, tape.Address);
            else if (p.EntryDecision is "INVEST_NOW" or "ARM")
                _tape.Watch(tape.Chain, tape.Address, a.Symbol, a.PairAddress, TimeSpan.FromMinutes(15));
        }

        private void RefreshIntelIfStale(TokenTape tape, Ctx ctx)
        {
            if (ctx.IntelRefreshing || (DateTime.UtcNow - ctx.IntelAt).TotalMinutes < 10) return;
            var cached = _intel.TryGetCached(tape.Chain, tape.Address);
            if (cached != null) { ctx.Intel = cached; ctx.IntelAt = DateTime.UtcNow; return; }

            ctx.IntelRefreshing = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var fresh = await _intel.GetAsync(tape.Chain, tape.Address);
                    if (fresh != null) ctx.Intel = fresh;
                }
                catch { }
                finally
                {
                    ctx.IntelAt = DateTime.UtcNow;
                    ctx.IntelRefreshing = false;
                }
            });
        }
    }
}
