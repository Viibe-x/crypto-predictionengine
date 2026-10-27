namespace DexScannerBot.Services
{
    /// <summary>
    /// Online logistic model: P(spike) = σ(bias + Σ wᵢ·xᵢ).
    ///
    /// Starts from hand-set prior weights (domain knowledge about what precedes memecoin
    /// spikes) and learns from every resolved outcome with stochastic gradient descent,
    /// regularised toward the prior so a handful of outcomes cannot swing it wildly.
    /// Learning the bias term is what makes the probability scale honest over time: if
    /// calls at "70%" only spike 40% of the time, the model pulls itself down.
    /// Thread-safe.
    /// </summary>
    public sealed class SpikeModel
    {
        // ── Feature names ─────────────────────────────────────────────────
        public const string Flow5m = "flow_5m";
        public const string Flow1h = "flow_1h";
        public const string TxBurst = "tx_burst";
        public const string VolBurst = "vol_burst";
        public const string VolAccel = "vol_accel";
        public const string LiveBuyAccel = "live_buy_accel";
        public const string LiveFlow = "live_flow";
        public const string LiveSlope = "live_slope";
        public const string LiveCurve = "live_curve";
        public const string LiveBreakout = "live_breakout";
        public const string LiveLiq = "live_liq";
        public const string Coil = "coil";
        public const string LateChase = "late_chase";
        public const string LiqDepth = "liq_depth";
        public const string CapRoom = "cap_room";
        public const string AgeFresh = "age_fresh";
        public const string Security = "security";
        public const string HolderRisk = "holder_risk";
        public const string WashRisk = "wash_risk";
        public const string DumpRisk = "dump_risk";
        public const string Attention = "attention";
        public const string Socials = "socials";

        /// <summary>Prior weights. Negative weights are for risk features whose value is ≥ 0.</summary>
        public static readonly IReadOnlyDictionary<string, double> PriorWeights = new Dictionary<string, double>
        {
            [Flow5m] = 0.85,
            [Flow1h] = 0.35,
            [TxBurst] = 0.75,
            [VolBurst] = 0.50,
            [VolAccel] = 0.30,
            [LiveBuyAccel] = 0.70,
            [LiveFlow] = 0.40,
            [LiveSlope] = 0.45,
            [LiveCurve] = 0.35,
            [LiveBreakout] = 0.35,
            [LiveLiq] = 0.35,
            [Coil] = 0.80,
            [LateChase] = -1.10,
            [LiqDepth] = 0.25,
            [CapRoom] = 0.25,
            [AgeFresh] = 0.20,
            [Security] = 0.50,
            [HolderRisk] = -0.60,
            [WashRisk] = -0.90,
            [DumpRisk] = -1.20,
            [Attention] = 0.15,
            [Socials] = 0.25,
        };

        public const double PriorBias = -3.4;

        /// <summary>Human-readable names for the dashboard.</summary>
        public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
        {
            [Flow5m] = "Buy flow 5m",
            [Flow1h] = "Buy flow 1h",
            [TxBurst] = "Buy-count burst",
            [VolBurst] = "Volume burst 5m",
            [VolAccel] = "Volume accel 1h",
            [LiveBuyAccel] = "Live buy acceleration",
            [LiveFlow] = "Live net flow",
            [LiveSlope] = "Live price slope",
            [LiveCurve] = "Live slope curvature",
            [LiveBreakout] = "Breakout over 5m high",
            [LiveLiq] = "Liquidity change",
            [Coil] = "Accumulation coil",
            [LateChase] = "Already ran (late)",
            [LiqDepth] = "Liquidity depth",
            [CapRoom] = "Market-cap room",
            [AgeFresh] = "Token freshness",
            [Security] = "Contract & LP security",
            [HolderRisk] = "Holder concentration",
            [WashRisk] = "Wash / bot volume",
            [DumpRisk] = "Dump / drain pressure",
            [Attention] = "Boost attention",
            [Socials] = "Web & socials",
        };

        private const double LearningRate = 0.04;
        private const double L2TowardPrior = 0.02;
        private const double MaxWeight = 4.0;

        private readonly object _lock = new();
        private readonly Dictionary<string, double> _w;
        private double _b;

        public int Updates { get; private set; }

        public SpikeModel(IDictionary<string, double>? weights = null, double? bias = null, int updates = 0)
        {
            _w = new Dictionary<string, double>(PriorWeights, StringComparer.OrdinalIgnoreCase);
            if (weights != null)
                foreach (var kv in weights)
                    if (_w.ContainsKey(kv.Key) && double.IsFinite(kv.Value)) _w[kv.Key] = Math.Clamp(kv.Value, -MaxWeight, MaxWeight);
            _b = bias.HasValue && double.IsFinite(bias.Value) ? bias.Value : PriorBias;
            Updates = updates;
        }

        public static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-Math.Clamp(z, -30, 30)));

        /// <summary>Log-odds and per-feature contributions (wᵢ·xᵢ).</summary>
        public double Logit(IReadOnlyDictionary<string, double> x, out Dictionary<string, double> contributions)
        {
            contributions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
            {
                double z = _b;
                foreach (var kv in x)
                {
                    if (!_w.TryGetValue(kv.Key, out var w) || !double.IsFinite(kv.Value)) continue;
                    double c = w * kv.Value;
                    contributions[kv.Key] = c;
                    z += c;
                }
                return z;
            }
        }

        public double Predict(IReadOnlyDictionary<string, double> x) => Sigmoid(Logit(x, out _));

        /// <summary>One SGD step on a resolved outcome (y = 1 spike, 0 no spike).</summary>
        public void Update(IReadOnlyDictionary<string, double> x, int y)
        {
            lock (_lock)
            {
                double z = _b;
                foreach (var kv in x)
                    if (_w.TryGetValue(kv.Key, out var w) && double.IsFinite(kv.Value)) z += w * kv.Value;
                double p = Sigmoid(z);
                double err = y - p;

                foreach (var name in _w.Keys.ToList())
                {
                    double xi = x.TryGetValue(name, out var v) && double.IsFinite(v) ? v : 0;
                    double prior = PriorWeights[name];
                    double grad = err * xi - L2TowardPrior * (_w[name] - prior);
                    _w[name] = Math.Clamp(_w[name] + LearningRate * grad, -MaxWeight, MaxWeight);
                }
                _b = Math.Clamp(_b + LearningRate * (err - L2TowardPrior * (_b - PriorBias)), -10, 10);
                Updates++;
            }
        }

        public (Dictionary<string, double> Weights, double Bias) Export()
        {
            lock (_lock) return (new Dictionary<string, double>(_w, StringComparer.OrdinalIgnoreCase), _b);
        }

        public double WeightOf(string name)
        {
            lock (_lock) return _w.TryGetValue(name, out var w) ? w : 0;
        }

        /// <summary>Features whose learned weight moved furthest from the prior.</summary>
        public List<(string Name, double Prior, double Learned)> Drift(int top = 6)
        {
            lock (_lock)
            {
                return _w.Select(kv => (kv.Key, PriorWeights[kv.Key], kv.Value))
                    .OrderByDescending(t => Math.Abs(t.Item3 - t.Item2))
                    .Take(top)
                    .ToList();
            }
        }
    }
}
