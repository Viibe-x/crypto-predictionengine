using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    /// <summary>
    /// Fetches REAL, externally-verified security data for a token.
    ///   - Solana  → RugCheck API  (https://api.rugcheck.xyz)   — free, no key
    ///   - EVM     → GoPlus Token Security API (https://api.gopluslabs.io) — free, no key
    ///
    /// Results are cached for a TTL so repeated scans don't hammer the APIs.
    /// On any failure/rate-limit it returns null so the caller treats security as
    /// "unknown" rather than inventing data. Crash-proof by design.
    /// </summary>
    public class SecurityIntelService
    {
        // EVM chain id → GoPlus numeric chain id
        private static readonly Dictionary<string, string> GoPlusChainIds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ethereum"] = "1",
            ["bsc"]      = "56",
            ["polygon"]  = "137",
            ["arbitrum"] = "42161",
            ["avalanche"]= "43114",
            ["base"]     = "8453",
            ["optimism"] = "10",
            ["fantom"]   = "250",
            ["linea"]    = "59144",
            ["blast"]    = "81457",
            ["mantle"]   = "5000",
            ["celo"]     = "42220",
            ["aurora"]   = "1313161554",
            ["cronos"]   = "25",
        };

        // Addresses that hold LP/supply on purpose (burn sinks).
        private static readonly HashSet<string> BurnAddresses = new(StringComparer.OrdinalIgnoreCase)
        {
            "0x000000000000000000000000000000000000dead",
            "0x0000000000000000000000000000000000000000",
            "0xdead000000000000000042069420694206942069",
        };

        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, (SecurityIntel Intel, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        private static string Env(string key, string d) => Environment.GetEnvironmentVariable(key) ?? d;
        public bool Enabled { get; }

        public int CachedCount => _cache.Count;
        public long TotalFetches { get; private set; }
        public long TotalFailures { get; private set; }

        public SecurityIntelService()
        {
            Enabled = !bool.TryParse(Env("DEXSB_SECURITY_ENABLED", "true"), out var v) || v;
            _http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                AllowAutoRedirect = true
            });
            _http.Timeout = TimeSpan.FromSeconds(6);
        }

        /// <summary>Cached intel only (no network). Null if not cached or expired.</summary>
        public SecurityIntel? TryGetCached(string chainId, string tokenAddress)
        {
            string key = chainId.ToLowerInvariant() + ":" + tokenAddress.ToLowerInvariant();
            return _cache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.At) < CacheTtl ? hit.Intel : null;
        }

        /// <summary>
        /// Returns verified security intel for a token, or null if it couldn't be
        /// determined (offline / unknown chain / not cached & fetch failed).
        /// </summary>
        public async Task<SecurityIntel?> GetAsync(string chainId, string tokenAddress, CancellationToken ct = default)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(tokenAddress) || string.IsNullOrWhiteSpace(chainId))
                return null;

            string key = chainId.ToLowerInvariant() + ":" + tokenAddress.ToLowerInvariant();
            if (_cache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.At) < CacheTtl)
                return hit.Intel;

            var intel = new SecurityIntel { TokenAddress = tokenAddress, Chain = chainId };
            bool ok;
            if (chainId.Equals("solana", StringComparison.OrdinalIgnoreCase))
                ok = await TryRugCheckAsync(intel, tokenAddress, ct);
            else if (GoPlusChainIds.TryGetValue(chainId, out var gId))
                ok = await TryGoPlusAsync(intel, gId, tokenAddress, ct);
            else
                return null; // unsupported chain for external intel

            TotalFetches++;
            if (!ok)
            {
                TotalFailures++;
                return null; // don't cache a failure — retry next scan
            }

            intel.FetchedAt = DateTime.UtcNow;
            _cache[key] = (intel, DateTime.UtcNow);
            if (_cache.Count > 5000)
                foreach (var kv in _cache.Where(k => DateTime.UtcNow - k.Value.At > CacheTtl).Take(2000).ToList())
                    _cache.TryRemove(kv.Key, out _);
            return intel;
        }

        public void Invalidate(string chainId, string tokenAddress)
        {
            _cache.TryRemove(chainId.ToLowerInvariant() + ":" + tokenAddress.ToLowerInvariant(), out _);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Solana → RugCheck
        // ─────────────────────────────────────────────────────────────────────
        private async Task<bool> TryRugCheckAsync(SecurityIntel intel, string tokenAddress, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.rugcheck.xyz/v1/tokens/{tokenAddress}/report");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)res.StatusCode != 200) return false;
                var json = await res.Content.ReadAsStringAsync(ct);
                ParseRugCheck(json, intel);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Parses a RugCheck v1 report into <paramref name="intel"/>. Public for tests.</summary>
        public static void ParseRugCheck(string json, SecurityIntel intel)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            intel.Source = "rugcheck";

            if (root.TryGetProperty("score_normalised", out var sn) && sn.ValueKind == JsonValueKind.Number && sn.TryGetInt32(out var score))
                intel.RugCheckScore = score;
            if (root.TryGetProperty("rugged", out var rg) && rg.ValueKind == JsonValueKind.True)
                intel.IsRugged = true;
            if (root.TryGetProperty("graphInsidersDetected", out var gi) && gi.ValueKind == JsonValueKind.Number && gi.TryGetInt32(out var insiders))
                intel.InsiderNetworkCount = insiders;
            if (root.TryGetProperty("totalHolders", out var th) && th.ValueKind == JsonValueKind.Number && th.TryGetInt32(out var holdersCount))
                intel.TotalHolders = holdersCount;

            // Contract authorities
            if (root.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.Object)
            {
                if (tok.TryGetProperty("mintAuthority", out var ma) && ma.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ma.GetString()))
                    intel.MintAuthorityActive = true;
                if (tok.TryGetProperty("freezeAuthority", out var fa) && fa.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fa.GetString()))
                    intel.FreezeAuthorityActive = true;
            }

            // Token-2022 transfer fee behaves like a tax in both directions.
            if (root.TryGetProperty("transferFee", out var tf) && tf.ValueKind == JsonValueKind.Object
                && tf.TryGetProperty("pct", out var tfp) && tfp.ValueKind == JsonValueKind.Number)
            {
                double pct = tfp.GetDouble();
                if (pct > 0) { intel.BuyTaxPct = pct; intel.SellTaxPct = pct; }
            }

            // Markets: LP lock of the deepest pool + every pool/vault account (to exclude from holders)
            var poolAccounts = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("markets", out var markets) && markets.ValueKind == JsonValueKind.Array)
            {
                double bestWeight = -1, lockOfDeepest = -1, bestLock = -1;
                foreach (var m in markets.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in m.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var s = prop.Value.GetString();
                            if (!string.IsNullOrEmpty(s) && s.Length >= 32 && s.Length <= 44) poolAccounts.Add(s);
                        }

                    if (!m.TryGetProperty("lp", out var lp) || lp.ValueKind != JsonValueKind.Object) continue;
                    if (lp.TryGetProperty("lpLockedPct", out var ll) && ll.ValueKind == JsonValueKind.Number)
                    {
                        double lockPct = ll.GetDouble();
                        bestLock = Math.Max(bestLock, lockPct);
                        double weight = lp.TryGetProperty("quoteUSD", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetDouble() : 0;
                        if (weight > bestWeight) { bestWeight = weight; lockOfDeepest = lockPct; }
                    }
                }
                double chosen = lockOfDeepest >= 0 ? lockOfDeepest : bestLock;
                intel.LpLockedPct = chosen >= 0 ? Math.Clamp(chosen, 0, 100) : -1;
            }

            // Holders: top-10 share excluding pool vaults / bonding curve, plus creator share
            string creator = root.TryGetProperty("creator", out var cr) && cr.ValueKind == JsonValueKind.String ? cr.GetString() ?? "" : "";
            if (root.TryGetProperty("topHolders", out var holders) && holders.ValueKind == JsonValueKind.Array)
            {
                var rows = new List<(string Address, string Owner, double Pct)>();
                foreach (var h in holders.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object) continue;
                    string addr = h.TryGetProperty("address", out var ad) && ad.ValueKind == JsonValueKind.String ? ad.GetString() ?? "" : "";
                    string owner = h.TryGetProperty("owner", out var ow) && ow.ValueKind == JsonValueKind.String ? ow.GetString() ?? "" : "";
                    double pct = h.TryGetProperty("pct", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : 0;
                    rows.Add((addr, owner, pct));
                }
                if (rows.Count > 0)
                {
                    bool fractions = rows.Sum(r => r.Pct) <= 1.5; // tolerate 0–1 scale
                    double scale = fractions ? 100 : 1;
                    var real = rows.Where(r => !poolAccounts.Contains(r.Address) && !poolAccounts.Contains(r.Owner)).ToList();
                    intel.Top10HolderPct = Math.Round(real.Take(10).Sum(r => r.Pct) * scale, 2);
                    if (!string.IsNullOrEmpty(creator))
                        intel.CreatorHolderPct = Math.Round(real.Where(r => r.Owner == creator || r.Address == creator).Sum(r => r.Pct) * scale, 2);
                }
            }

            // Named risk items — only warn/danger levels count as risks
            if (root.TryGetProperty("risks", out var risks) && risks.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in risks.EnumerateArray())
                {
                    if (!r.TryGetProperty("name", out var nm) || nm.ValueKind != JsonValueKind.String) continue;
                    string level = r.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.String ? (lv.GetString() ?? "") : "warn";
                    if (level.Equals("info", StringComparison.OrdinalIgnoreCase)) continue;
                    intel.ProviderRisks.Add(nm.GetString()!);
                    if (level.Equals("danger", StringComparison.OrdinalIgnoreCase)) intel.DangerRiskCount++;
                }
            }

            DeriveContractFlags(intel);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EVM → GoPlus Token Security
        // ─────────────────────────────────────────────────────────────────────
        private async Task<bool> TryGoPlusAsync(SecurityIntel intel, string chainNumeric, string tokenAddress, CancellationToken ct)
        {
            try
            {
                var uri = $"https://api.gopluslabs.io/api/v1/token_security/{chainNumeric}?contract_addresses={Uri.EscapeDataString(tokenAddress)}";
                using var res = await _http.GetAsync(uri, ct);
                if ((int)res.StatusCode != 200) return false;
                var json = await res.Content.ReadAsStringAsync(ct);
                return ParseGoPlus(json, tokenAddress, intel);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Parses a GoPlus token_security response. Public for tests.</summary>
        public static bool ParseGoPlus(string json, string tokenAddress, SecurityIntel intel)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return false;
            if (!result.TryGetProperty(tokenAddress.ToLowerInvariant(), out var t) && !result.TryGetProperty(tokenAddress, out t)) return false;

            intel.Source = "goplus";
            static bool IsTrue(JsonElement e) =>
                (e.ValueKind == JsonValueKind.String && e.GetString() == "1") || (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) && i == 1);
            bool Flag(string name) => t.TryGetProperty(name, out var e) && IsTrue(e);

            intel.IsHoneypot = Flag("is_honeypot");
            intel.OwnerCanChangeBalance = Flag("owner_change_balance");
            intel.HasHiddenOwner = Flag("hidden_owner");
            intel.IsProxy = Flag("is_proxy");
            intel.TransferPausable = Flag("transfer_pausable");
            intel.MintAuthorityActive = Flag("is_mintable");

            if (Flag("cannot_sell_all")) { intel.ProviderRisks.Add("CANNOT_SELL_ALL"); intel.DangerRiskCount++; }
            if (Flag("can_take_back_ownership")) { intel.ProviderRisks.Add("CAN_TAKE_BACK_OWNERSHIP"); intel.DangerRiskCount++; }
            if (Flag("slippage_modifiable")) { intel.ProviderRisks.Add("TAX_MODIFIABLE"); intel.DangerRiskCount++; }
            if (Flag("is_blacklisted")) intel.ProviderRisks.Add("BLACKLIST_FUNCTION");
            if (Flag("trading_cooldown")) intel.ProviderRisks.Add("TRADING_COOLDOWN");
            if (Flag("personal_slippage_modifiable")) { intel.ProviderRisks.Add("PER_WALLET_TAX"); intel.DangerRiskCount++; }

            // Taxes come back as fractions in strings, e.g. "0.05" = 5%, "1" = 100%.
            if (t.TryGetProperty("buy_tax", out var bt)) intel.BuyTaxPct = ParseFractionPct(bt);
            if (t.TryGetProperty("sell_tax", out var st)) intel.SellTaxPct = ParseFractionPct(st);

            // LP lock: share of LP tokens that are locked or sit in a burn address.
            if (t.TryGetProperty("lp_holders", out var lpHolders) && lpHolders.ValueKind == JsonValueKind.Array && lpHolders.GetArrayLength() > 0)
            {
                double locked = 0;
                foreach (var h in lpHolders.EnumerateArray())
                {
                    double pct = h.TryGetProperty("percent", out var pe) ? ParseFractionPct(pe) : 0;
                    if (pct < 0) continue;
                    bool isLocked = h.TryGetProperty("is_locked", out var il) && IsTrue(il);
                    string addr = h.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
                    if (isLocked || BurnAddresses.Contains(addr)) locked += pct;
                }
                intel.LpLockedPct = Math.Clamp(Math.Round(locked, 1), 0, 100);
            }

            // Holder concentration (percent is a fraction string, e.g. "0.12" = 12%)
            if (t.TryGetProperty("holders", out var holders) && holders.ValueKind == JsonValueKind.Array)
            {
                string creator = t.TryGetProperty("creator_address", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() ?? "" : "";
                double top = 0, creatorPct = 0;
                int counted = 0;
                foreach (var h in holders.EnumerateArray())
                {
                    if (counted >= 10) break;
                    string addr = h.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
                    double pct = h.TryGetProperty("percent", out var pe) ? ParseFractionPct(pe) : -1;
                    if (pct < 0) continue;
                    bool locked = h.TryGetProperty("is_locked", out var il) && IsTrue(il);
                    if (locked || BurnAddresses.Contains(addr)) continue; // locked/burned supply is not sell pressure
                    top += pct;
                    counted++;
                    if (creator.Length > 0 && addr.Equals(creator, StringComparison.OrdinalIgnoreCase)) creatorPct += pct;
                }
                intel.Top10HolderPct = Math.Round(top, 2);
                intel.CreatorHolderPct = Math.Round(creatorPct, 2);
            }
            if (t.TryGetProperty("holder_count", out var hc) && hc.ValueKind == JsonValueKind.String && int.TryParse(hc.GetString(), out var hcount))
                intel.TotalHolders = hcount;

            if (intel.IsHoneypot) intel.DangerRiskCount++;
            if (intel.OwnerCanChangeBalance) intel.DangerRiskCount++;
            DeriveContractFlags(intel);
            return true;
        }

        /// <summary>GoPlus encodes shares as fractions ("0.05" = 5%). Returns percent, or -1 if unreadable.</summary>
        private static double ParseFractionPct(JsonElement e)
        {
            double v;
            if (e.ValueKind == JsonValueKind.Number) v = e.GetDouble();
            else if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) v = s;
            else return -1;
            return v <= 1.0 ? v * 100.0 : v;
        }

        /// <summary>Adds canonical risk names for the raw contract flags.</summary>
        private static void DeriveContractFlags(SecurityIntel intel)
        {
            if (intel.IsRugged) AddRisk(intel, "RUGGED");
            if (intel.IsHoneypot) AddRisk(intel, "HONEYPOT");
            if (intel.MintAuthorityActive) AddRisk(intel, "MINT_AUTHORITY_ACTIVE");
            if (intel.FreezeAuthorityActive) AddRisk(intel, "FREEZE_AUTHORITY_ENABLED");
            if (intel.HasHiddenOwner) AddRisk(intel, "HIDDEN_OWNER");
            if (intel.OwnerCanChangeBalance) AddRisk(intel, "OWNER_CAN_CHANGE_BALANCE");
            if (intel.IsProxy) AddRisk(intel, "PROXY_CONTRACT");
            if (intel.SellTaxPct >= 10) AddRisk(intel, "HIGH_SELL_TAX");
            if (intel.Top10HolderPct >= 40) AddRisk(intel, "HIGH_HOLDER_CONCENTRATION");
            if (intel.LpLockedPct >= 0 && intel.LpLockedPct < 50) AddRisk(intel, "LP_NOT_LOCKED");
        }

        private static void AddRisk(SecurityIntel intel, string flag)
        {
            if (!intel.ProviderRisks.Contains(flag)) intel.ProviderRisks.Add(flag);
        }
    }
}
