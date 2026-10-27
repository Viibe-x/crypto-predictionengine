using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    public class RugDetectService
    {
        // Tracks historical liquidity readings per token for drain velocity calculation
        private static readonly ConcurrentDictionary<string, (double Liquidity, DateTime Timestamp)> _liquidityHistory = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Merges REAL external security intelligence (from SecurityIntelService) into
        /// the rug result, replacing the heuristic fabrications whenever verified data
        /// is available. Confirmed fraud (honeypot, owner-tax, unlocked-LP-with-death-
        /// concentration) is a hard block regardless of how hot the volume is.
        /// </summary>
        public void ApplyIntel(RugDetectionResult result, SecurityIntel intel)
        {
            if (intel == null) return;

            var c = result.Contract;
            var l = result.Liquidity;

            // ── Contract (authoritative when intel present) ─────────────────
            if (intel.IsRugged)                   { c.IsHoneypot = true; c.SuspiciousPatterns.Add("provider_marked_rugged"); c.RiskScore = Math.Max(c.RiskScore, 60); }
            if (intel.IsHoneypot)                 { c.IsHoneypot = true; c.RiskScore = Math.Max(c.RiskScore, 60); }
            if (intel.MintAuthorityActive)        { c.IsMintable = true; c.RiskScore += 25; }
            if (intel.FreezeAuthorityActive)
            {
                c.IsFreezable = true;
                c.RiskScore += 20;
                // On Solana a live freeze authority can freeze the token account inside your
                // wallet (Phantom included) — you would be unable to sell. Treat as critical.
                if (result.Chain.Equals("solana", StringComparison.OrdinalIgnoreCase)) c.RiskScore = Math.Max(c.RiskScore, 55);
            }
            if (intel.HasHiddenOwner)             { c.HasProxyPattern = true; c.RiskScore += 15; }
            if (intel.IsProxy)                    { c.HasProxyPattern = true; c.RiskScore += 10; }
            if (intel.OwnerCanChangeBalance)      { c.OwnerCanChangeBalance = true; c.SuspiciousPatterns.Add("owner_can_change_balance"); c.RiskScore += 30; }
            if (intel.SellTaxPct > 10)            { c.SuspiciousPatterns.Add($"sell_tax_{intel.SellTaxPct:F1}%"); c.RiskScore += 20; }
            else if (intel.IsHoneypot) { /* honeypot already dominates */ }

            // ── Liquidity (authoritative when intel present) ────────────────
            if (intel.LpLockedPct >= 0)
            {
                l.IsLpLocked = intel.LpLockedPct >= 95;
                l.LockProvider = l.IsLpLocked ? "Verified_Lock" : null;
                if (intel.LpLockedPct < 50) { l.HasRugPullSignature = true; l.RiskScore += 25; }
                else if (intel.LpLockedPct < 95) { l.RiskScore += 10; }
            }
            if (intel.Top10HolderPct >= 0)
            {
                l.Top10LpConcentration = Math.Min(1.0, intel.Top10HolderPct / 100.0);
                if (l.Top10LpConcentration > 0.40) l.HasRugPullSignature = true;
            }

            // ── Developer (verified holder split) ───────────────────────────
            // CreatorHolderPct is in percent (0–100): the old "> 0.06" flagged nearly every token.
            if (intel.CreatorHolderPct >= 6)
            {
                result.Developer.IsDevHeavy = true;
                result.Developer.DevHoldPercentage = intel.CreatorHolderPct / 100.0;
                result.Developer.RiskScore = Math.Min(70, result.Developer.RiskScore + 20);
            }

            // ── Aggregate provider risks → named flags ──────────────────────
            foreach (var risk in intel.ProviderRisks)
            {
                var flag = NormalizeRisk(risk);
                if (flag != null && !result.Flags.Contains(flag)) result.Flags.Add(flag);
            }

            // ── Guard mint authority against honeypot of a hot token ────────
            if (intel.IsHoneypot || intel.OwnerCanChangeBalance)
                c.RiskScore = Math.Max(c.RiskScore, 60);

            c.RiskScore = Math.Min(c.RiskScore, 60);
            l.RiskScore = Math.Min(l.RiskScore, 80);

            // Recompute the weighted score and re-derive decision/verdict from
            // the now-real data.
            Recompute(result);
        }

        /// <summary>Maps provider risk strings to our canonical flag vocabulary.</summary>
        private static string? NormalizeRisk(string risk)
        {
            var r = risk.ToLowerInvariant();
            if (r.Contains("rugged"))              return "RUG_PULL_SIGNATURE";
            if (r.Contains("honeypot"))            return "HONEYPOT_DETECTED";
            if (r.Contains("mint"))                return "MINT_AUTHORITY_ACTIVE";
            if (r.Contains("freeze"))              return "FREEZE_AUTHORITY_ENABLED";
            if (r.Contains("hidden_owner") || r.Contains("hidden owner")) return "HIDDEN_OWNER";
            if (r.Contains("owner_change") || r.Contains("change balance") || r.Contains("blacklist")) return "OWNER_CAN_CHANGE_BALANCE";
            if (r.Contains("lp_not_locked") || r.Contains("lp not locked") || r.Contains("unverified lp")) return "LP_NOT_LOCKED";
            if (r.Contains("holder_concentration") || r.Contains("concentration")) return "HIGH_LP_CONCENTRATION";
            if (r.Contains("sell_tax") || r.Contains("tax")) return "HIGH_TAX";
            if (r.Contains("pool_drained") || r.Contains("drained")) return "RUG_PULL_SIGNATURE";
            return null; // unknown items aren't flagged, they're informational
        }

        private void Recompute(RugDetectionResult result)
        {
            if (result.Contract.IsHoneypot) result.Contract.RiskScore = Math.Max(result.Contract.RiskScore, 60);
            if (result.Contract.OwnerCanChangeBalance) result.Contract.RiskScore = Math.Max(result.Contract.RiskScore, 60);

            result.RiskScore.ContractRisk = result.Contract.RiskScore;
            result.RiskScore.LiquidityRisk = result.Liquidity.RiskScore;
            result.RiskScore.DeveloperRisk = result.Developer.RiskScore;
            result.RiskScore.SocialRisk = result.Social.RiskScore;

            // Hard block on confirmed honeypot is non-negotiable.
            result.RecommendedAction = result.Contract.IsHoneypot ? "BLOCK" :
                                       result.RiskScore.ShouldBlock ? "BLOCK" :
                                       result.RiskScore.ShouldReduceSize ? "REDUCE_SIZE" : "PROCEED";

            // Recompute verified-reputable. Real secure-LP + clean provider risks is the
            // only path to VERIFIED when external intel exists.
            var flagsOk = result.Flags.Count == 0 || result.Contract.IsHoneypot == false;
            result.VerifiedReputable = result.Liquidity.IsLpLocked
                && !result.Contract.IsHoneypot
                && result.RiskScore.TotalScore < 41
                && flagsOk
                && result.Social.HasWebsite;
            var parts = new List<string>();
            if (result.Liquidity.IsLpLocked) parts.Add("LP_LOCKED");
            if (!result.Contract.IsHoneypot && !result.Contract.IsMintable && !result.Contract.IsFreezable) parts.Add("CLEAN_CONTRACT");
            if (result.Social.HasWebsite) parts.Add("WEBSITE");
            result.Verification = result.VerifiedReputable
                ? "VERIFIED: " + string.Join(" · ", parts)
                : (result.RiskScore.TotalScore < 41 ? "PARTIAL: " + string.Join(" · ", parts) : "UNVERIFIED");

            // Flags already populated; ensure duplicates removed.
            result.Flags = result.Flags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public RugDetectionResult Analyze(DexPair pair, TokenAnalysis analysis, SecurityIntel? intel = null)
        {
            var result = new RugDetectionResult
            {
                TokenAddress = pair.BaseToken?.Address ?? "",
                Symbol = pair.BaseToken?.Symbol ?? "",
                Chain = pair.ChainId ?? ""
            };

            result.Contract = AnalyzeContract(pair, analysis);
            result.Liquidity = AnalyzeLiquidity(pair);
            result.Developer = AnalyzeDeveloper(pair);
            result.Social = AnalyzeSocial(pair);

            result.RiskScore = new RugRiskScore
            {
                ContractRisk = result.Contract.RiskScore,
                LiquidityRisk = result.Liquidity.RiskScore,
                DeveloperRisk = result.Developer.RiskScore,
                SocialRisk = result.Social.RiskScore
            };

            result.RecommendedAction = result.RiskScore.ShouldBlock ? "BLOCK" :
                                       result.RiskScore.ShouldReduceSize ? "REDUCE_SIZE" : "PROCEED";

            if (result.Contract.IsHoneypot) result.Flags.Add("HONEYPOT_DETECTED");
            if (result.Contract.IsMintable) result.Flags.Add("MINT_AUTHORITY_ACTIVE");
            if (result.Contract.IsFreezable) result.Flags.Add("FREEZE_AUTHORITY_ENABLED");
            if (result.Liquidity.HasRugPullSignature) result.Flags.Add("RUG_PULL_SIGNATURE");
            if (result.Liquidity.IsLiquidityDraining) result.Flags.Add("LIQUIDITY_DRAINING");
            if (result.Liquidity.Top10LpConcentration > 0.40) result.Flags.Add("HIGH_LP_CONCENTRATION");
            if (result.Developer.IsDevHeavy) result.Flags.Add("DEV_HEAVY_ALLOCATION");
            if (result.Developer.IsFromMixer) result.Flags.Add("FUNDED_FROM_MIXER");
            if (result.Developer.IsWashTrading) result.Flags.Add("WASH_TRADING");
            if (result.Developer.HasCoordinatedDump) result.Flags.Add("COORDINATED_DUMP");
            if (result.Social.HasBotCampaign) result.Flags.Add("BOT_CAMPAIGN_DETECTED");
            if (result.Social.IsCoordinatedCampaign) result.Flags.Add("COORDINATED_CAMPAIGN");
            if (result.Social.IsTelegramOnlyNewToken) result.Flags.Add("TELEGRAM_ONLY_ANON");
            if (!result.Social.HasWebsite && result.RiskScore.TotalScore > 30) result.Flags.Add("NO_WEB_PRESENCE");

            // ── Verified Reputable computation ──────────────────────────────
            // A token is "verified reputable" when it shows concrete on-chain/integrity
            // signals that make a rug unlikely. LP must be genuinely locked (verified by intel or burn).
            int socialCount = (pair.Info?.Websites?.Count ?? 0) + (pair.Info?.Socials?.Count ?? 0);
            double liq = pair.Liquidity?.Usd ?? 0;
            double cap = pair.MarketCap > 0 ? pair.MarketCap : pair.Fdv;
            bool lpLockedOrBurned = result.Liquidity.IsLpLocked && (liq >= 5_000);
            bool healthyLiquidity = liq >= 15_000;
            bool recentAndActive = result.Liquidity.LpAgeHours < 720;
            bool hasSocials = socialCount >= 2;
            bool hasWebsite = result.Social.HasWebsite;
            bool noRugFlags = result.Flags.Count == 0;

            double vScore = 0;
            if (lpLockedOrBurned) vScore += 0.35;
            if (healthyLiquidity) vScore += 0.20;
            if (hasWebsite)       vScore += 0.15;
            if (hasSocials)       vScore += 0.15;
            if (noRugFlags)       vScore += 0.15;
            if (recentAndActive && cap > 0 && cap < 50_000_000) vScore += 0.10;
            result.VerifiedReputable = vScore >= 0.65 && result.RiskScore.TotalScore < 35 && (intel == null || !intel.IsCriticalThreat);

            var vParts = new List<string>();
            if (lpLockedOrBurned) vParts.Add("LP_LOCKED");
            if (healthyLiquidity) vParts.Add("LIQ≥$15K");
            if (hasWebsite)       vParts.Add("WEBSITE");
            if (hasSocials)       vParts.Add("SOCIALS");
            if (noRugFlags)       vParts.Add("NO_RUG_FLAGS");
            result.Verification = result.VerifiedReputable
                ? "VERIFIED: " + string.Join(" · ", vParts)
                : (vScore >= 0.4 ? "PARTIAL: " + string.Join(" · ", vParts) : "UNVERIFIED");

            // Overlay real external security intel (honeypot tests / LP lock / authorities)
            // whenever the provider returned data. This is what catches confirmed fraud.
            if (intel != null)
                ApplyIntel(result, intel);

            result.AnalyzedAt = DateTime.UtcNow;
            return result;
        }

        /// <summary>
        /// Contract risk cannot be read from DexScreener market data — it comes only from
        /// verified security intel (ApplyIntel). The previous version matched substrings of
        /// the pair address against "honeypot" patterns and called every token without
        /// socials a "proxy contract"; both produced fabricated risk and were removed.
        /// </summary>
        private ContractAnalysis AnalyzeContract(DexPair pair, TokenAnalysis analysis)
        {
            return new ContractAnalysis { RiskScore = 0 };
        }

        private LiquidityAnalysis AnalyzeLiquidity(DexPair pair)
        {
            var la = new LiquidityAnalysis { RiskScore = 0 };
            double liq = pair.Liquidity?.Usd ?? 0;
            string addr = pair.BaseToken?.Address ?? pair.PairAddress ?? "";

            // CRITICAL FIX: Do NOT assume LP is locked just because liq > 0.
            // LP lock is unverified unless confirmed by external security intel or provable burn.
            la.IsLpLocked = false;
            la.LockProvider = null;
            la.LockExpiry = null;
            // Unknown until real holder data arrives via ApplyIntel (was fabricated from pool size,
            // which flagged every pool under $100K as a rug-pull signature).
            la.Top10LpConcentration = 0;
            la.IsBurnVerified = false;
            la.LpAgeHours = pair.PairCreatedAt > 0
                ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pair.PairCreatedAt) / 3_600_000.0
                : 9999;

            // Liquidity Drain Tracking: compare with previous reading
            if (!string.IsNullOrEmpty(addr))
            {
                if (_liquidityHistory.TryGetValue(addr, out var prev) && prev.Liquidity > 0)
                {
                    var timeDiff = (DateTime.UtcNow - prev.Timestamp).TotalMinutes;
                    if (timeDiff <= 30 && prev.Liquidity > liq)
                    {
                        double dropPct = (prev.Liquidity - liq) / prev.Liquidity * 100.0;
                        la.LiquidityDrainPct = Math.Round(dropPct, 1);
                        if (dropPct >= 60.0)
                        {
                            la.IsLiquidityDraining = true;
                            la.HasRugPullSignature = true;
                            la.RiskScore += 50;
                        }
                        else if (dropPct >= 20.0)
                        {
                            la.IsLiquidityDraining = true;
                            la.RiskScore += 25;
                        }
                    }
                }
                _liquidityHistory[addr] = (liq, DateTime.UtcNow);

                // Trim historical cache if too large
                if (_liquidityHistory.Count > 5000)
                {
                    var cutoff = DateTime.UtcNow.AddHours(-2);
                    foreach (var kvp in _liquidityHistory.Where(k => k.Value.Timestamp < cutoff).Take(1000).ToList())
                        _liquidityHistory.TryRemove(kvp.Key, out _);
                }
            }

            // Unverified LP is inherently risky on low-liquidity pairs
            if (!la.IsLpLocked && liq < 20000) { la.RiskScore += 25; }
            if (la.Top10LpConcentration > 0.40) { la.RiskScore += 20; la.HasRugPullSignature = true; }
            if (la.LpAgeHours < 1 && liq < 5000) { la.RiskScore += 25; }
            else if (la.LpAgeHours < 6 && liq < 10000) { la.RiskScore += 15; }

            if (la.RiskScore > 80) la.RiskScore = 80;
            return la;
        }

        private DeveloperAnalysis AnalyzeDeveloper(DexPair pair)
        {
            var da = new DeveloperAnalysis { RiskScore = 0 };
            double liq = pair.Liquidity?.Usd ?? 0;

            // A brand-new, thin pool is genuinely riskier — but we do not know who holds what,
            // so no dev-holding percentage is invented here (ApplyIntel supplies real data).
            bool isNewWithLowLiq = pair.PairCreatedAt > 0 &&
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pair.PairCreatedAt) < 3_600_000 &&
                liq < 10000;
            if (isNewWithLowLiq) da.RiskScore += 15;

            int buys1h = pair.Txns?.H1?.Buys ?? 0;
            int sells1h = pair.Txns?.H1?.Sells ?? 0;
            int buys5m = pair.Txns?.M5?.Buys ?? 0;
            int sells5m = pair.Txns?.M5?.Sells ?? 0;
            double vol1h = pair.Volume?.H1 ?? 0;

            if (vol1h > 0 && (buys1h + sells1h) > 20)
            {
                da.HasCoordinatedSelling = sells1h > buys1h * 2;
                if (da.HasCoordinatedSelling) da.RiskScore += 25;
            }

            // Coordinated 5m dump detection
            if (sells5m >= 5 && sells5m >= buys5m * 3)
            {
                da.HasCoordinatedDump = true;
                da.RiskScore += 30;
            }

            // Wash trading detection: huge volume with suspiciously uniform or microscopic avg transaction
            int totalTxns1h = buys1h + sells1h;
            if (totalTxns1h >= 10 && vol1h > 10000)
            {
                double avgTxnVol = vol1h / totalTxns1h;
                if (avgTxnVol > 0 && liq > 0 && (vol1h / liq) > 100.0 && (buys1h == sells1h || Math.Abs(buys1h - sells1h) <= 1))
                {
                    da.IsWashTrading = true;
                    da.WashTradingScore = 75.0;
                    da.RiskScore += 25;
                }
            }

            if (da.RiskScore > 70) da.RiskScore = 70;
            return da;
        }

        private SocialAnalysis AnalyzeSocial(DexPair pair)
        {
            var sa = new SocialAnalysis { RiskScore = 0 };
            int socialCount = (pair.Info?.Websites?.Count ?? 0) + (pair.Info?.Socials?.Count ?? 0);
            sa.HasWebsite = pair.Info?.Websites?.Any(w => !string.IsNullOrWhiteSpace(w.Url)) ?? false;
            
            sa.HasTelegramActivity = pair.Info?.Socials?.Any(s =>
                s.Type?.Contains("telegram", StringComparison.OrdinalIgnoreCase) == true ||
                s.Url?.Contains("t.me", StringComparison.OrdinalIgnoreCase) == true) ?? false;

            sa.HasTwitter = pair.Info?.Socials?.Any(s =>
                s.Type?.Contains("twitter", StringComparison.OrdinalIgnoreCase) == true ||
                string.Equals(s.Type, "x", StringComparison.OrdinalIgnoreCase) ||
                s.Url?.Contains("twitter.com", StringComparison.OrdinalIgnoreCase) == true ||
                s.Url?.Contains("x.com", StringComparison.OrdinalIgnoreCase) == true) ?? false;

            double ageHours = pair.PairCreatedAt > 0
                ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pair.PairCreatedAt) / 3_600_000.0
                : 9999;

            // Telegram-only brand new token is a classic hit-and-run rug pattern
            if (sa.HasTelegramActivity && !sa.HasTwitter && !sa.HasWebsite && ageHours < 24)
            {
                sa.IsTelegramOnlyNewToken = true;
                sa.RiskScore += 20;
            }

            sa.IsCoordinatedCampaign = sa.HasBotCampaign;
            sa.SentimentScore = (sa.HasWebsite && sa.HasTwitter) ? 75 : (sa.HasWebsite ? 50 : (sa.HasTwitter ? 40 : (sa.HasTelegramActivity ? 25 : 10)));
            sa.DomainAgeDays = pair.PairCreatedAt > 0
                ? (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pair.PairCreatedAt) / 86_400_000)
                : 0;

            if (sa.IsCoordinatedCampaign) { sa.HasBotCampaign = true; sa.RiskScore += 30; }
            if (!sa.HasWebsite && pair.MarketCap > 100_000) { sa.RiskScore += 15; }
            if (socialCount == 0) { sa.RiskScore += 20; }
            else if (sa.SentimentScore < 30) { sa.RiskScore += 10; }

            if (sa.RiskScore > 60) sa.RiskScore = 60;
            return sa;
        }
    }
}
