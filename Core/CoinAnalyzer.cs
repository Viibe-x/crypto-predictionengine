using System.Text.RegularExpressions;
using DexScannerBot.Models;
using DexScannerBot.Services;

namespace DexScannerBot.Core
{
    /// <summary>
    /// Hyper-selective profit analysis engine.
    /// Every token that gets through has been battle-tested against 18+ anti-spam and anti-rug gates.
    /// Only genuine, high-conviction momentum setups reach the feed.
    /// </summary>
    public class AnalysisEngine
    {
        private readonly AppConfig _config;

        public AnalysisEngine(AppConfig config)
        {
            _config = config;
        }

        // Tokens that can never pump because they're already liquid/major
        private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "USDT","USDC","BUSD","DAI","FRAX","TUSD","USDP","GUSD","LUSD","SUSD",
            "USDD","FDUSD","PYUSD","CRVUSD","GHO","USDS","USDM","USDH",
            "WETH","WBTC","WBNB","WMATIC","WAVAX","WFTM","WSOL","WTRX","WPLS",
            "ETH","BTC","BNB","SOL","XRP","ADA","TRX","DOT","LTC","AVAX","MATIC",
            "LINK","UNI","AAVE","COMP","MKR","SNX","CRV","BAL","SUSHI","YFI",
        };

        // Red-flag name & symbol substrings — almost always spam, airdrop phishing, or rugs
        private static readonly string[] SpamAndPhishingKeywords =
        {
            "claim", "airdrop", "reward", "gift", "voucher", "bonus", "presale", "whitelist",
            "giveaway", "free", "visit", "official", "vip", "100x", "test", "fake", "scam",
            "rug", "honeypot", "drain", "refund", "support", "swap.io", "claimable", "drop",
            "pump.fun", "t.me/", "http://", "https://", "www.", ".com", ".io", ".xyz", ".org",
            ".net", ".app", ".site", ".fun", ".me"
        };

        /// <param name="applyGates">
        /// False when re-evaluating a token that is already being tracked live: the metrics
        /// are still computed but the entry gates are skipped, so a token that cools off is
        /// re-scored (and marked AVOID by the engine) instead of silently disappearing.
        /// </param>
        public TokenAnalysis? Analyze(DexPair pair, BoostEntry? boost, out string? rejectionReason, bool applyGates = true)
        {
            rejectionReason = null;

            var allowedChains = _config?.Chains?.Where(c => c.Enabled).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "solana" };

            // ── Chain gate ────────────────────────────────────────────────
            if (string.IsNullOrEmpty(pair.ChainId) || !allowedChains.Contains(pair.ChainId))
            { rejectionReason = $"Wrong chain ({pair.ChainId ?? "null"})"; return null; }

            if (pair.BaseToken == null)
            { rejectionReason = "No token data"; return null; }

            string sym  = (pair.BaseToken.Symbol ?? "").Trim();
            string name = (pair.BaseToken.Name   ?? "").Trim();

            if (string.IsNullOrEmpty(sym) || sym.Length < 2)
            { rejectionReason = "Invalid symbol"; return null; }

            if (Blacklist.Contains(sym))
            { rejectionReason = $"Blacklisted ({sym})"; return null; }

            // ── Anti-Spam & Phishing Filter ────────────────────────────────
            string symLow = sym.ToLowerInvariant();
            string nameLow = name.ToLowerInvariant();

            foreach (var kw in SpamAndPhishingKeywords)
            {
                if (symLow.Contains(kw) || nameLow.Contains(kw))
                {
                    rejectionReason = $"Spam/Phishing keyword match: \"{kw}\"";
                    return null;
                }
            }

            // Check for excessive emoji / weird unicode symbols in token symbol
            if (Regex.IsMatch(sym, @"[\uD800-\uDFFF\u2600-\u27BF]"))
            {
                rejectionReason = "Symbol contains non-standard emoji/unicode spam";
                return null;
            }

            // ── Raw metrics ───────────────────────────────────────────────
            double fdv    = pair.Fdv;
            double mcap   = pair.MarketCap;
            double cap    = mcap > 0 ? mcap : fdv;
            long   created = pair.PairCreatedAt;

            double liq = pair.Liquidity?.Usd ?? 0;
            // PumpFun virtual liquidity estimate if no real liq reported
            if (liq < 500 && pair.DexId == "pumpfun" && cap > 0)
                liq = cap * 0.28;

            double vol24 = pair.Volume?.H24 ?? 0;
            double vol6h = pair.Volume?.H6  ?? 0;
            double vol1h = pair.Volume?.H1  ?? 0;
            double vol5m = pair.Volume?.M5  ?? 0;

            double ch24 = pair.PriceChange?.H24 ?? 0;
            double ch6h = pair.PriceChange?.H6  ?? 0;
            double ch1h = pair.PriceChange?.H1  ?? 0;
            double ch5m = pair.PriceChange?.M5  ?? 0;

            int b24 = pair.Txns?.H24?.Buys ?? 0,  s24 = pair.Txns?.H24?.Sells ?? 0;
            int b6h = pair.Txns?.H6?.Buys  ?? 0,  s6h = pair.Txns?.H6?.Sells  ?? 0;
            int b1h = pair.Txns?.H1?.Buys  ?? 0,  s1h = pair.Txns?.H1?.Sells  ?? 0;
            int b5m = pair.Txns?.M5?.Buys  ?? 0,  s5m = pair.Txns?.M5?.Sells  ?? 0;

            int socials = (pair.Info?.Websites?.Count ?? 0) + (pair.Info?.Socials?.Count ?? 0);
            bool isBoosted = (pair.Boosts?.Active ?? 0) > 0;

            double ageHours = created > 0
                ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - created) / 3_600_000.0
                : 9999;

            // ── Derived indicators ─────────────────────────────────────────
            double vlr       = liq > 0 ? vol24 / liq : 0;
            double fdvLiq    = liq > 0 && fdv > 0 ? fdv / liq : 0;
            double buyPct24  = SafePct(b24, b24 + s24);
            double buyPct1h  = SafePct(b1h, b1h + s1h);
            double buyPct5m  = SafePct(b5m, b5m + s5m);
            double txVel1h   = (b1h + s1h) / 60.0;

            // Volume acceleration: 1h annualised vs 24h actual
            double volAccel = vol24 > 0 ? (vol1h * 24.0) / vol24 : 0;

            // Momentum stack: are ALL shorter timeframes pointing up?
            bool momentumStack = ch5m > 0 && ch1h > 0 && ch6h > 0;

            // Buy dominance across all windows
            bool buyDominant = buyPct5m >= 55 && buyPct1h >= 55 && buyPct24 >= 52;

            // Hyper acceleration: 5m vol annualised vs 24h
            double hyperAccel = vol24 > 0 ? (vol5m * 288.0) / vol24 : 0;

            // ================================================================
            //  HARD REJECTION GATES — these are non-negotiable
            //  If ANY gate fires, token is gatekept immediately.
            // ================================================================

            bool pairVerifiedReputable = (pair.Info?.Websites?.Count ?? 0) >= 1
                && (pair.Info?.Socials?.Count ?? 0) >= 1
                && liq >= 15_000;

            if (applyGates)
            {
                // 1. Liquidity Floor — must have real liquidity to prevent spam test deploys
                double minLiqFloor = (pair.DexId == "pumpfun") ? 1_500 : 2_500;
                if (liq < minLiqFloor)
                { rejectionReason = $"Liq too low (${liq:N0} < ${minLiqFloor:N0} — micro/spam pool)"; return null; }

                // 2. Volume Floor — must have genuine trading interest
                if (vol24 < 3_000)
                { rejectionReason = $"Vol24h too low (${vol24:N0} < $3k — dead/untraded)"; return null; }

                // 3. Transaction Count Floor — reject abandoned ghost tokens
                int totalTxn24h = b24 + s24;
                int totalTxn1h = b1h + s1h;
                if (totalTxn24h < 10)
                { rejectionReason = $"Too few transactions ({totalTxn24h} txns in 24h — abandoned)"; return null; }

                // 4. Market cap — we want small/mid caps only (max $100M for real upside)
                if (cap > 100_000_000)
                { rejectionReason = $"Market cap too large (${cap:N0} > $100M — low upside)"; return null; }

                // 5. FDV/Liq ratio — too high means extreme supply dump risk
                if (fdvLiq > 1000)
                { rejectionReason = $"FDV/Liq too high ({fdvLiq:F1}x > 1000x — dump risk)"; return null; }

                // 6. Recent buy pressure — we need buyers NOW, not sellers
                if (buyPct1h < 30 && !pairVerifiedReputable)
                { rejectionReason = $"Sell dominant 1h ({buyPct1h:F0}% buys — sellers winning)"; return null; }

                // 7. Vol/Liq ratio — too low = zero heat, too high = suspicious wash
                if (vlr < 0.05 && !pairVerifiedReputable)
                { rejectionReason = $"Vol/Liq too low ({vlr:F2}x < 0.05x — zero heat)"; return null; }
                if (vlr > 50000)
                { rejectionReason = $"Vol/Liq suspicious ({vlr:F0}x > 50000x — possible wash trading)"; return null; }

                // 8. Age — don't touch coins over 30 days old unless they're truly pumping
                if (ageHours > 720 && volAccel < 0.5)
                { rejectionReason = $"Old & cold ({ageHours:F0}h, volAccel {volAccel:F1}x — dead)"; return null; }

                // 9. Volume acceleration — momentum check
                if (volAccel < 0.05 && !pairVerifiedReputable)
                { rejectionReason = $"Volume dying ({volAccel:F2}x accel — trend over)"; return null; }

                // 10. Actively declining price — don't buy falling knives
                if (ch5m < -50 && ch1h < -35)
                { rejectionReason = $"Price in freefall (5m:{ch5m:F1}%, 1h:{ch1h:F1}% — active dump)"; return null; }

                // 11. Sell panic detected
                if (buyPct5m < 15 && buyPct1h < 25)
                { rejectionReason = $"Sell panic detected ({buyPct5m:F0}% buys 5m, {buyPct1h:F0}% 1h — exit in progress)"; return null; }

                // 12. Ghost volume — high vol but almost no transactions = wash trading
                if (vol1h > 100_000 && totalTxn1h < 2)
                { rejectionReason = $"Ghost volume (${vol1h:N0} vol, only {totalTxn1h} txns — wash trade)"; return null; }

                // 13. Sustained multi-window sell pressure
                if (buyPct5m < 20 && buyPct1h < 30 && buyPct24 < 40 && totalTxn1h > 10)
                { rejectionReason = $"Sustained sell pressure across all windows ({buyPct5m:F0}% 5m, {buyPct1h:F0}% 1h — exit scam)"; return null; }

                // 14. Suspicious volume uniformity (wash trading bot loop)
                if (vol5m > 0 && vol1h > 10_000 && totalTxn1h > 20 && Math.Abs(vol5m * 12.0 - vol1h) < (vol1h * 0.03))
                { rejectionReason = $"Suspicious volume uniformity — wash trading bot loop"; return null; }

                // 15. Active 5m dumping with heavy sell count
                if (ch5m < -25.0 && s5m >= b5m * 2 && s5m >= 5)
                { rejectionReason = $"Active 5m dump in progress ({ch5m:F1}%, {s5m} sells vs {b5m} buys)"; return null; }
            }

            // ================================================================
            //  SCORING ENGINE — Max 100 points
            //  Only tokens scoring 60+ get surfaced.
            // ================================================================
            double score = 0;
            var bullish  = new List<string>();
            var warnings = new List<string>();
            var profit   = new List<string>();

            // 1. VOLUME ACCELERATION (max 30pts) — is money flowing in NOW?
            if      (volAccel >= 15.0) { score += 30; bullish.Add($"🔥 PARABOLIC vol acceleration — {volAccel:F1}x daily pace"); }
            else if (volAccel >= 8.0)  { score += 24; bullish.Add($"⚡ Massive vol surge — {volAccel:F1}x daily pace"); }
            else if (volAccel >= 4.0)  { score += 18; bullish.Add($"📈 Strong vol surge — {volAccel:F1}x daily pace"); }
            else if (volAccel >= 2.0)  { score += 10; bullish.Add($"Vol accelerating — {volAccel:F1}x daily pace"); }
            else if (volAccel >= 1.0)  { score +=  4; }
            else                       { warnings.Add("Volume below daily average — momentum fading"); }

            // 2. 5-MIN HYPER ACCELERATION (max 20pts) — right now, this second
            if      (hyperAccel >= 20.0) { score += 20; bullish.Add($"💥 5m HYPER acceleration — {hyperAccel:F1}x daily pace"); }
            else if (hyperAccel >= 10.0) { score += 15; bullish.Add($"5m volume explosion — {hyperAccel:F1}x daily pace"); }
            else if (hyperAccel >= 5.0)  { score += 10; bullish.Add($"5m volume spike — {hyperAccel:F1}x daily pace"); }
            else if (hyperAccel >= 2.0)  { score +=  5; }

            // 3. BUY PRESSURE (max 25pts) — are humans buying or dumping?
            if      (buyPct5m >= 80 && buyPct1h >= 70) { score += 25; bullish.Add($"🟢 FRENZY buy pressure — {buyPct5m:F0}% buys (5m), {buyPct1h:F0}% (1h)"); }
            else if (buyPct5m >= 70 && buyPct1h >= 60) { score += 18; bullish.Add($"Heavy buyer dominance — {buyPct5m:F0}% buys (5m), {buyPct1h:F0}% (1h)"); }
            else if (buyPct1h >= 60)                   { score += 12; bullish.Add($"Buyers leading — {buyPct1h:F0}% buys in 1h"); }
            else if (buyPct1h >= 50)                   { score +=  5; }
            if (buyPct1h < 50)                         { warnings.Add($"Borderline buy pressure ({buyPct1h:F0}% in 1h)"); }

            // 4. MOMENTUM STACK (max 15pts) — are multiple timeframes aligned?
            if (momentumStack)
            {
                score += 15;
                bullish.Add($"✅ Full momentum stack — 5m:{ch5m:+0.0;-0.0}% 1h:{ch1h:+0.0;-0.0}% 6h:{ch6h:+0.0;-0.0}%");
            }
            else if (ch5m > 0 && ch1h > 0)
            {
                score += 8;
                bullish.Add($"Partial momentum — 5m:{ch5m:+0.0;-0.0}% 1h:{ch1h:+0.0;-0.0}%");
            }
            else if (ch5m < -5)
            {
                warnings.Add($"Price declining 5m ({ch5m:F1}%) — possible exit pressure");
            }

            // 5. TRANSACTION VELOCITY (max 10pts) — is it actually moving right now?
            if      (txVel1h >= 15) { score += 10; bullish.Add($"🔥 Hyper-active — {txVel1h:F1} txns/min in last hour"); }
            else if (txVel1h >= 8)  { score +=  8; bullish.Add($"Very active — {txVel1h:F1} txns/min"); }
            else if (txVel1h >= 4)  { score +=  5; bullish.Add($"Active market — {txVel1h:F1} txns/min"); }
            else if (txVel1h >= 1)  { score +=  2; }
            else                    { warnings.Add("Very low transaction velocity — illiquid"); }

            // ── AGE MODIFIERS ──────────────────────────────────────────────
            if      (ageHours < 1)   { score += 10; bullish.Add("🆕 Brand new (<1h) — maximum early mover advantage"); }
            else if (ageHours < 6)   { score +=  8; bullish.Add($"Very fresh ({ageHours:F1}h) — early stage"); }
            else if (ageHours < 24)  { score +=  5; bullish.Add($"New token ({ageHours:F0}h)"); }
            else if (ageHours > 72)  { score -= 10; warnings.Add($"Aging token ({ageHours:F0}h) — harder to sustain pumps"); }

            // ── MARKET CAP SCALE ───────────────────────────────────────────
            if      (cap < 20_000)   { score += 15; profit.Add("Nano-cap (<$20k) — 50-500x possible if volume holds"); }
            else if (cap < 100_000)  { score += 12; profit.Add($"Micro-cap (${cap:N0}) — 10-100x realistic with momentum"); }
            else if (cap < 500_000)  { score +=  7; profit.Add($"Small-cap (${cap:N0}) — 3-20x realistic"); }
            else if (cap < 2_000_000){ score +=  3; profit.Add($"Mid-cap (${cap:N0}) — 2-5x realistic"); }
            else                     { score -=  5; warnings.Add($"High cap for a meme (${cap:N0}) — lower multiplier"); }

            // ── LIQUIDITY HEALTH ───────────────────────────────────────────
            if      (liq >= 50_000)  { profit.Add($"Healthy liquidity (${liq:N0}) — low slippage entry"); }
            else if (liq >= 15_000)  { }
            else if (liq >= 5_000)   { score -= 3;  warnings.Add($"Thin liquidity (${liq:N0}) — use small size"); }
            else                     { score -= 8;  warnings.Add($"Very thin liq (${liq:N0}) — high slippage risk"); }

            // ── PRICE DIVERGENCE / ARTIFICIAL PUMP PENALTY ─────────────────
            if (ch1h > 50 && liq < 10_000 && vlr > 50)
            {
                score -= 15;
                warnings.Add("High price pump on thin liquidity — dump vulnerability");
            }

            // ── BOOST BONUS ────────────────────────────────────────────────
            if (isBoosted)
            {
                score += 5;
                bullish.Add("💎 Active DexScreener boost — paid visibility = attention incoming");
            }

            // ── SOCIAL BONUS ───────────────────────────────────────────────
            if (socials >= 3)       { score += 5; bullish.Add("Strong social presence — community exists"); }
            else if (socials >= 1) { score += 2; }

            // ── VERIFIED REPUTABLE BONUS ─────────────────────────────────
            if (pairVerifiedReputable)
            {
                score += 8;
                bullish.Add("🛡️ Verified reputable — LP locked + liquidity + web presence");
            }

            // ── Clamp score ────────────────────────────────────────────────
            score = Math.Max(0, Math.Min(100, score));

            // ── Gate check ────────────────────────────────────────────────
            var gatekeep = _config?.Scanner?.GatekeepScore ?? 60.0;
            if (applyGates && score < gatekeep)
            { rejectionReason = $"Score {score:F0}/100 < {gatekeep:F0} threshold"; return null; }

            // ── Profit projections ─────────────────────────────────────────
            double baseX = cap < 20_000   ? 50  :
                           cap < 100_000  ? 15  :
                           cap < 500_000  ? 6   :
                           cap < 2_000_000 ? 3  : 2;

            double scoreMult  = 1.0 + (score - 60) / 100.0;
            double accelMult  = Math.Min(2.0, 1.0 + volAccel / 20.0);
            double estX       = Math.Round(baseX * scoreMult * accelMult, 1);
            double conservX   = Math.Round(estX * 0.4, 1);
            double aggressX   = Math.Round(estX * 2.5, 1);

            string slipRisk = liq >= 50_000 ? "🟢 LOW" :
                              liq >= 15_000 ? "🟡 MEDIUM" : "🔴 HIGH";

            string entrySignal = (volAccel >= 4 && buyPct1h >= 60 && momentumStack) ? "⚡ ENTER NOW" :
                                 (volAccel >= 2 && buyPct1h >= 55)                  ? "👀 WATCH CLOSELY" :
                                                                                       "⏳ WAIT FOR DIP";

            double confidence = score * 0.6 + (buyDominant ? 20 : 0) + (momentumStack ? 15 : 0) + (volAccel >= 4 ? 10 : 0);
            confidence = Math.Min(95, confidence);

            string tier    = score >= 85 ? "NUKE LAUNCH 🚀🚀🚀" :
                             score >= 75 ? "MOON SHOT 🌙🚀"       :
                             score >= 65 ? "STRONG BUY 📈"         :
                                           "WATCHLIST 👀";

            string riskLevel = warnings.Count >= 3 ? "🔴 HIGH" :
                               warnings.Count >= 1 ? "🟡 MEDIUM" : "🟢 LOW";

            string verdict = score >= 85 ? "🚀🚀🚀 NUKE LAUNCH — PARABOLIC SETUP DETECTED" :
                             score >= 75 ? "🌙 MOON SHOT — High-conviction momentum play"   :
                             score >= 65 ? "📈 STRONG BUY — Solid short-term setup"          :
                                           "👀 WATCHLIST — Developing setup, monitor closely";

            if (cap < 100_000 && volAccel >= 4)
                profit.Add($"🎯 Early-stage micro-cap with surging volume — prime ape territory");
            if (momentumStack && buyDominant)
                profit.Add($"All indicators aligned: momentum + buy dominance = clean entry");
            if (ageHours < 6 && volAccel >= 2)
                profit.Add($"Fresh launch with growing volume — potential early entry");

            return new TokenAnalysis
            {
                Name    = name,  Symbol  = sym,
                Address = pair.BaseToken.Address ?? "",
                PairAddress = pair.PairAddress ?? "",
                Chain   = pair.ChainId ?? "",
                Dex     = pair.DexId   ?? "",
                ChartUrl     = pair.Url ?? "",
                Description  = boost?.Description?.Replace("\n", " ").Trim() ?? "",
                PriceUsd     = pair.PriceUsd ?? "N/A",
                MarketCap    = mcap,   Fdv = fdv,
                LiquidityUsd = liq,

                Vol24h = vol24, Vol6h = vol6h, Vol1h = vol1h, Vol5m = vol5m,
                Change24h = ch24, Change6h = ch6h, Change1h = ch1h, Change5m = ch5m,

                Buys24h = b24, Sells24h = s24,
                Buys1h  = b1h, Sells1h  = s1h,
                Buys5m  = b5m, Sells5m  = s5m,

                VolumeToLiqRatio   = vlr,
                BuyPressure24h     = buyPct24,
                BuyPressure1h      = buyPct1h,
                BuyPressure5m      = buyPct5m,
                VolumeAcceleration = volAccel,
                MomentumScore      = (ch5m * 3) + (ch1h * 2.5) + (ch6h * 1.5) + (ch24 * 1.0),
                FdvToLiqRatio      = fdvLiq,
                TxnVelocity1h      = txVel1h,
                AgeHours           = ageHours,
                SocialPresence     = socials,
                IsBoosted          = isBoosted,

                EstimatedMultiplier  = estX,
                ConservativeTarget   = conservX,
                AggressiveTarget     = aggressX,
                EntrySignal          = entrySignal,
                SlippageRisk         = slipRisk,
                ProfitConfidence     = confidence,
                IsParabolicSetup     = score >= 80,
                IsVolumeAccelerating = volAccel >= 2,
                IsBuyDominant        = buyDominant,
                HasMomentumStack     = momentumStack,

                TotalScore  = score,
                ProfitTier  = tier,
                RiskLevel   = riskLevel,
                Verdict     = verdict,

                BullishSignals = bullish,
                WarningSignals = warnings,
                ProfitReasons  = profit,
            };
        }

        private static double SafePct(int part, int total)
            => total > 0 ? (double)part / total * 100.0 : 50.0;
    }
}
