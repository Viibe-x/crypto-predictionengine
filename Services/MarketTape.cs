using System.Collections.Concurrent;
using System.Diagnostics;
using DexScannerBot.Core;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    /// <summary>One DexScreener snapshot of a token.</summary>
    public sealed class TapeTick
    {
        public DateTime At { get; init; }
        public double Price { get; init; }
        public double LiquidityUsd { get; init; }
        public double MarketCap { get; init; }
        public double Vol5m { get; init; }
        public double Vol1h { get; init; }
        public double Vol24h { get; init; }
        public int Buys5m { get; init; }
        public int Sells5m { get; init; }
        public int Buys1h { get; init; }
        public int Sells1h { get; init; }
        public int Buys24h { get; init; }
        public int Sells24h { get; init; }
        public double AgeHours { get; init; }

        public static TapeTick? From(DexPair p, DateTime at)
        {
            double price = DexJson.ParsePrice(p.PriceUsd);
            if (price <= 0) return null;
            return new TapeTick
            {
                At = at,
                Price = price,
                LiquidityUsd = p.Liquidity?.Usd ?? 0,
                MarketCap = p.MarketCap > 0 ? p.MarketCap : p.Fdv,
                Vol5m = p.Volume?.M5 ?? 0,
                Vol1h = p.Volume?.H1 ?? 0,
                Vol24h = p.Volume?.H24 ?? 0,
                Buys5m = p.Txns?.M5?.Buys ?? 0,
                Sells5m = p.Txns?.M5?.Sells ?? 0,
                Buys1h = p.Txns?.H1?.Buys ?? 0,
                Sells1h = p.Txns?.H1?.Sells ?? 0,
                Buys24h = p.Txns?.H24?.Buys ?? 0,
                Sells24h = p.Txns?.H24?.Sells ?? 0,
                AgeHours = p.PairCreatedAt > 0
                    ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - p.PairCreatedAt) / 3_600_000.0
                    : 9999
            };
        }
    }

    /// <summary>Rolling snapshot history for one token.</summary>
    public sealed class TokenTape
    {
        public string Key { get; init; } = "";
        public string Chain { get; init; } = "";
        public string Address { get; init; } = "";
        public string Symbol { get; set; } = "";
        public string PairAddress { get; set; } = "";
        public DateTime WatchUntil { get; set; }
        public DateTime LastTickAt { get; set; }
        public DexPair? LastPair { get; set; }
        internal readonly object Sync = new();
        internal readonly List<TapeTick> Ticks = new();
        public bool IsWatched => WatchUntil > DateTime.UtcNow;
    }

    /// <summary>
    /// The live market tape.
    ///
    /// DexScreener only exposes fixed rolling windows (5m / 1h / 6h / 24h). A spike usually
    /// starts inside a single minute: buy transactions burst well above their hourly pace
    /// while price is still flat, then price breaks the recent high. Those short-window
    /// dynamics are invisible in one snapshot, so this service re-polls every watched token
    /// every few seconds (batched, ≤30 addresses per call, well under the 300 req/min limit)
    /// and differences successive snapshots to measure:
    ///   • buys/sells per minute in the last ~60s vs the hourly baseline (buy-rate acceleration)
    ///   • $ volume per minute in the last ~60s vs the hourly baseline
    ///   • price slope (short vs long → curvature), breakout over the 5-minute high
    ///   • liquidity change (adds = fuel, pulls = danger), tick volatility, drawdown from high
    /// Every scanned pair is also ingested for free, so tokens accumulate history even
    /// before they qualify for the watchlist.
    /// </summary>
    public sealed class MarketTape
    {
        private const int MaxTicksPerToken = 180;
        private static readonly TimeSpan MaxTickAge = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan IdleExpiry = TimeSpan.FromMinutes(45);

        private readonly PredictionConfig _cfg;
        private readonly ConcurrentDictionary<string, TokenTape> _tapes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _fetchLock = new(1, 1);
        private DateTime _lastFetch = DateTime.MinValue;

        /// <summary>Raised after a watched token receives a fresh tick from the poll loop.</summary>
        public event Action<TokenTape, DexPair>? OnTick;

        public int WatchedCount => _tapes.Values.Count(t => t.IsWatched);
        public int TrackedCount => _tapes.Count;
        public long TotalPolls { get; private set; }
        public double LastPollLatencyMs { get; private set; }

        public MarketTape(PredictionConfig cfg)
        {
            _cfg = cfg;
            _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
            _http.DefaultRequestHeaders.Add("User-Agent", "DexScannerBot/4.0 (Win64)");
            _http.Timeout = TimeSpan.FromSeconds(10);
        }

        public static string KeyOf(string? chain, string? address)
            => (chain ?? "").ToLowerInvariant() + ":" + (address ?? "").ToLowerInvariant();

        /// <summary>Adds a snapshot to the token's tape. Safe to call from any thread.</summary>
        public TokenTape? Ingest(DexPair pair) => Ingest(pair, DateTime.UtcNow);

        public TokenTape? Ingest(DexPair pair, DateTime at)
        {
            var addr = pair.BaseToken?.Address;
            if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(pair.ChainId)) return null;
            var tick = TapeTick.From(pair, at);
            if (tick == null) return null;

            string key = KeyOf(pair.ChainId, addr);
            var tape = _tapes.GetOrAdd(key, _ => new TokenTape
            {
                Key = key,
                Chain = pair.ChainId!.ToLowerInvariant(),
                Address = addr!,
                Symbol = pair.BaseToken?.Symbol ?? "",
                PairAddress = pair.PairAddress ?? ""
            });

            // Track the pair we first saw; ignore snapshots of a different (smaller) pool
            // for the same token so the tape never mixes two price series.
            if (!string.IsNullOrEmpty(tape.PairAddress) && !string.IsNullOrEmpty(pair.PairAddress)
                && !tape.PairAddress.Equals(pair.PairAddress, StringComparison.OrdinalIgnoreCase))
                return tape;

            lock (tape.Sync)
            {
                if (tape.Ticks.Count > 0)
                {
                    var last = tape.Ticks[tape.Ticks.Count - 1];
                    if (at <= last.At) return tape; // out-of-order
                    // Same data re-reported within 2s → replace rather than append.
                    if ((at - last.At).TotalSeconds < 2) tape.Ticks[tape.Ticks.Count - 1] = tick;
                    else tape.Ticks.Add(tick);
                }
                else tape.Ticks.Add(tick);

                var cutoff = at - MaxTickAge;
                int drop = 0;
                while (drop < tape.Ticks.Count - 2 && tape.Ticks[drop].At < cutoff) drop++;
                if (tape.Ticks.Count - drop > MaxTicksPerToken) drop = tape.Ticks.Count - MaxTicksPerToken;
                if (drop > 0) tape.Ticks.RemoveRange(0, drop);
            }
            if (string.IsNullOrEmpty(tape.PairAddress)) tape.PairAddress = pair.PairAddress ?? "";
            tape.LastTickAt = at;
            tape.LastPair = pair;
            return tape;
        }

        /// <summary>Puts a token on the fast-poll watchlist (or extends its stay).</summary>
        public void Watch(string chain, string address, string symbol, string pairAddress, TimeSpan ttl)
        {
            if (string.IsNullOrWhiteSpace(chain) || string.IsNullOrWhiteSpace(address)) return;
            string key = KeyOf(chain, address);
            var tape = _tapes.GetOrAdd(key, _ => new TokenTape
            {
                Key = key,
                Chain = chain.ToLowerInvariant(),
                Address = address,
                Symbol = symbol,
                PairAddress = pairAddress ?? ""
            });
            var until = DateTime.UtcNow + ttl;
            if (until > tape.WatchUntil) tape.WatchUntil = until;
            if (string.IsNullOrEmpty(tape.Symbol)) tape.Symbol = symbol;

            // Enforce the watchlist cap: drop the ones closest to expiry.
            var watched = _tapes.Values.Where(t => t.IsWatched).ToList();
            if (watched.Count > _cfg.MaxWatched)
            {
                foreach (var t in watched.OrderBy(t => t.WatchUntil).Take(watched.Count - _cfg.MaxWatched))
                    if (t.Key != key) t.WatchUntil = DateTime.MinValue;
            }
        }

        public void Unwatch(string chain, string address)
        {
            if (_tapes.TryGetValue(KeyOf(chain, address), out var t)) t.WatchUntil = DateTime.MinValue;
        }

        public bool IsWatched(string chain, string address)
            => _tapes.TryGetValue(KeyOf(chain, address), out var t) && t.IsWatched;

        public TapeFeatures? GetFeatures(string chain, string address)
            => _tapes.TryGetValue(KeyOf(chain, address), out var t) ? Compute(t) : null;

        /// <summary>Fast-poll loop for watched tokens — call once from Program.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            int pollSeconds = Math.Max(4, _cfg.TapePollSeconds);
            while (!ct.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                try
                {
                    await PollOnceAsync(ct);
                    Housekeep();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Console.WriteLine($"[TAPE] Poll error: {ex.Message}"); }

                var wait = TimeSpan.FromSeconds(pollSeconds) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task PollOnceAsync(CancellationToken ct)
        {
            var watched = _tapes.Values.Where(t => t.IsWatched).ToList();
            if (watched.Count == 0) return;

            foreach (var group in watched.GroupBy(t => t.Chain))
            {
                foreach (var batch in group.Chunk(30))
                {
                    string raw;
                    await _fetchLock.WaitAsync(ct);
                    try
                    {
                        // ≥250ms between calls keeps the tape far below DexScreener's 300/min.
                        var since = DateTime.UtcNow - _lastFetch;
                        if (since < TimeSpan.FromMilliseconds(250)) await Task.Delay(TimeSpan.FromMilliseconds(250) - since, ct);
                        var url = $"https://api.dexscreener.com/tokens/v1/{group.Key}/{string.Join(",", batch.Select(b => b.Address))}";
                        var sw = Stopwatch.StartNew();
                        raw = await _http.GetStringAsync(url, ct);
                        LastPollLatencyMs = sw.Elapsed.TotalMilliseconds;
                        _lastFetch = DateTime.UtcNow;
                        TotalPolls++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { continue; } // transient — next cycle retries
                    finally { _fetchLock.Release(); }

                    var pairs = DexJson.ParsePairs(raw);
                    if (pairs.Count == 0) continue;
                    var now = DateTime.UtcNow;

                    foreach (var tape in batch)
                    {
                        var candidates = pairs.Where(p => string.Equals(p.BaseToken?.Address, tape.Address, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (candidates.Count == 0) continue;
                        var pair = candidates.FirstOrDefault(p => string.Equals(p.PairAddress, tape.PairAddress, StringComparison.OrdinalIgnoreCase))
                                   ?? candidates.OrderByDescending(p => p.Liquidity?.Usd ?? 0).First();
                        if (Ingest(pair, now) == null) continue;
                        try { OnTick?.Invoke(tape, pair); }
                        catch (Exception ex) { Console.WriteLine($"[TAPE] Tick handler error ({tape.Symbol}): {ex.Message}"); }
                    }
                }
            }
        }

        private void Housekeep()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _tapes)
            {
                if (!kv.Value.IsWatched && now - kv.Value.LastTickAt > IdleExpiry)
                    _tapes.TryRemove(kv.Key, out _);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Feature computation
        // ─────────────────────────────────────────────────────────────────

        public static TapeFeatures Compute(TokenTape tape)
        {
            List<TapeTick> ticks;
            lock (tape.Sync) ticks = tape.Ticks.ToList();
            return Compute(ticks);
        }

        /// <summary>Computes live features from a chronological list of ticks.</summary>
        public static TapeFeatures Compute(IReadOnlyList<TapeTick> ticks)
        {
            var f = new TapeFeatures { Samples = ticks.Count };
            if (ticks.Count == 0) return f;

            f.Spark = ticks.Skip(Math.Max(0, ticks.Count - 40)).Select(t => t.Price).ToList();
            var last = ticks[ticks.Count - 1];
            f.SpanSeconds = (last.At - ticks[0].At).TotalSeconds;
            f.BaselineBuyRatePerMin = last.Buys1h / 60.0;
            if (ticks.Count < 3 || f.SpanSeconds < 20) return f;

            f.TickSeconds = f.SpanSeconds / (ticks.Count - 1);

            // ── Flow over the most recent ~60s ─────────────────────────────
            var refTick = LatestAtOrBefore(ticks, last.At.AddSeconds(-60)) ?? ticks[0];
            double dtMin = (last.At - refTick.At).TotalMinutes;
            if (dtMin < 0.25) return f;

            bool young = last.AgeHours < 23.5;
            double buys = CountDelta(last.Buys24h, refTick.Buys24h, last.Buys5m, refTick.Buys5m, dtMin, young);
            double sells = CountDelta(last.Sells24h, refTick.Sells24h, last.Sells5m, refTick.Sells5m, dtMin, young);
            double vol = AmountDelta(last.Vol24h, refTick.Vol24h, last.Vol5m, refTick.Vol5m, dtMin, young);

            f.BuyRatePerMin = buys / dtMin;
            f.SellRatePerMin = sells / dtMin;
            f.BuyRateAccel = (f.BuyRatePerMin + 0.3) / (f.BaselineBuyRatePerMin + 0.3);
            f.RecentTxns = (int)Math.Round(buys + sells);
            f.NetFlowPct = buys + sells > 0 ? buys / (buys + sells) * 100.0 : 50;
            f.VolumeRatePerMin = vol / dtMin;
            f.VolumeBurst = (f.VolumeRatePerMin + 1) / (last.Vol1h / 60.0 + 1);

            // ── Price structure ────────────────────────────────────────────
            f.SlopeShortPctPerMin = LogSlopePctPerMin(ticks, last.At.AddSeconds(-60));
            f.SlopeLongPctPerMin = LogSlopePctPerMin(ticks, last.At.AddMinutes(-4));
            f.PriceSlopePctPerMin = LogSlopePctPerMin(ticks, last.At.AddMinutes(-2));

            var win3 = ticks.Where(t => t.At >= last.At.AddMinutes(-3)).Select(t => t.Price).ToList();
            if (win3.Count >= 2)
            {
                double lo = win3.Min(), hi = win3.Max();
                f.RangePct = lo > 0 ? (hi - lo) / lo * 100 : 0;
            }

            var prior = ticks.Where(t => t.At >= last.At.AddMinutes(-5) && t.At <= last.At.AddSeconds(-25)).ToList();
            if (prior.Count >= 2)
                f.Breakout = last.Price > prior.Max(t => t.Price) * 1.003;

            var win5 = ticks.Where(t => t.At >= last.At.AddMinutes(-5)).ToList();
            double high5 = win5.Max(t => t.Price);
            f.DrawFromHighPct = high5 > 0 ? (high5 - last.Price) / high5 * 100 : 0;

            var liqRef = LatestAtOrBefore(ticks, last.At.AddMinutes(-3)) ?? ticks[0];
            f.LiqChangePct = liqRef.LiquidityUsd > 0 ? (last.LiquidityUsd - liqRef.LiquidityUsd) / liqRef.LiquidityUsd * 100 : 0;

            var recent = ticks.Skip(Math.Max(0, ticks.Count - 16)).ToList();
            if (recent.Count >= 3)
            {
                var rets = new List<double>();
                for (int i = 1; i < recent.Count; i++)
                    if (recent[i - 1].Price > 0 && recent[i].Price > 0)
                        rets.Add(Math.Log(recent[i].Price / recent[i - 1].Price));
                if (rets.Count >= 2)
                {
                    double m = rets.Average();
                    f.TickVolatilityPct = Math.Sqrt(rets.Sum(r => (r - m) * (r - m)) / (rets.Count - 1)) * 100;
                }
            }

            f.HasLive = true;
            Sanitize(f);
            return f;
        }

        /// <summary>
        /// New events between two snapshots. For tokens younger than 24h the h24 counter is
        /// cumulative, so its difference is exact. Otherwise the 5-minute rolling counter is
        /// used: new ≈ (m5_now − m5_then) + m5_then·Δt/5 (events that rolled out of the window).
        /// </summary>
        public static double CountDelta(int h24Now, int h24Then, int m5Now, int m5Then, double dtMin, bool young)
        {
            double fromH24 = h24Now - h24Then;
            double fromM5 = (m5Now - m5Then) + m5Then * Math.Min(1.0, dtMin / 5.0);
            if (young && fromH24 >= 0) return fromH24;
            if (fromH24 >= 0 && fromM5 >= 0) return (fromH24 + fromM5) / 2.0;
            return Math.Max(0, Math.Max(fromH24, fromM5));
        }

        public static double AmountDelta(double h24Now, double h24Then, double m5Now, double m5Then, double dtMin, bool young)
        {
            double fromH24 = h24Now - h24Then;
            double fromM5 = (m5Now - m5Then) + m5Then * Math.Min(1.0, dtMin / 5.0);
            if (young && fromH24 >= 0) return fromH24;
            if (fromH24 >= 0 && fromM5 >= 0) return (fromH24 + fromM5) / 2.0;
            return Math.Max(0, Math.Max(fromH24, fromM5));
        }

        private static TapeTick? LatestAtOrBefore(IReadOnlyList<TapeTick> ticks, DateTime at)
        {
            for (int i = ticks.Count - 1; i >= 0; i--)
                if (ticks[i].At <= at) return ticks[i];
            return null;
        }

        /// <summary>Least-squares slope of ln(price) vs minutes since <paramref name="from"/>, in % per minute.</summary>
        private static double LogSlopePctPerMin(IReadOnlyList<TapeTick> ticks, DateTime from)
        {
            var pts = ticks.Where(t => t.At >= from && t.Price > 0).ToList();
            if (pts.Count < 2) return 0;
            var t0 = pts[0].At;
            double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var p in pts)
            {
                double x = (p.At - t0).TotalMinutes, y = Math.Log(p.Price);
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double den = n * sxx - sx * sx;
            if (Math.Abs(den) < 1e-12) return 0;
            double slope = (n * sxy - sx * sy) / den;
            return (Math.Exp(slope) - 1) * 100;
        }

        private static void Sanitize(TapeFeatures f)
        {
            static double S(double v) => double.IsFinite(v) ? v : 0;
            f.BuyRatePerMin = S(f.BuyRatePerMin);
            f.SellRatePerMin = S(f.SellRatePerMin);
            f.BaselineBuyRatePerMin = S(f.BaselineBuyRatePerMin);
            f.BuyRateAccel = S(f.BuyRateAccel);
            f.NetFlowPct = S(f.NetFlowPct);
            f.VolumeRatePerMin = S(f.VolumeRatePerMin);
            f.VolumeBurst = S(f.VolumeBurst);
            f.PriceSlopePctPerMin = S(f.PriceSlopePctPerMin);
            f.SlopeShortPctPerMin = S(f.SlopeShortPctPerMin);
            f.SlopeLongPctPerMin = S(f.SlopeLongPctPerMin);
            f.RangePct = S(f.RangePct);
            f.LiqChangePct = S(f.LiqChangePct);
            f.DrawFromHighPct = S(f.DrawFromHighPct);
            f.TickVolatilityPct = S(f.TickVolatilityPct);
            f.TickSeconds = S(f.TickSeconds);
        }
    }
}
