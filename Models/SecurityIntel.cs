namespace DexScannerBot.Models
{
    /// <summary>
    /// Real, externally-verified security intelligence for a token.
    /// Aggregated from RugCheck (Solana) and GoPlus (EVM chains).
    /// Every field here comes from an actual on-chain/security API —
    /// nothing is inferred or invented like the old heuristic layer.
    /// </summary>
    public class SecurityIntel
    {
        public string TokenAddress { get; set; } = "";
        public string Chain        { get; set; } = "";

        /// <summary>"rugcheck", "goplus", or "rugcheck+goplus"</summary>
        public string Source { get; set; } = "";

        // ── Contract / token-program dangers ──────────────────────────────
        public bool IsHoneypot            { get; set; }
        public bool MintAuthorityActive   { get; set; }
        public bool FreezeAuthorityActive { get; set; }
        public bool HasHiddenOwner        { get; set; }
        public bool IsProxy               { get; set; }
        public bool OwnerCanChangeBalance { get; set; }
        public bool TransferPausable      { get; set; }

        /// <summary>Buy tax in percent (0-100). -1 if unknown.</summary>
        public double BuyTaxPct  { get; set; } = -1;
        /// <summary>Sell tax in percent (0-100). -1 if unknown.</summary>
        public double SellTaxPct { get; set; } = -1;

        // ── Liquidity / holders ───────────────────────────────────────────
        /// <summary>Percent of LP that is locked or burned (0-100). -1 if unknown.</summary>
        public double LpLockedPct { get; set; } = -1;
        /// <summary>Top-10 holder share of supply (0-100). -1 if unknown.</summary>
        public double Top10HolderPct { get; set; } = -1;
        /// <summary>Creator/dev share of supply (0-100). -1 if unknown.</summary>
        public double CreatorHolderPct { get; set; } = -1;

        // ── Aggregate risk ────────────────────────────────────────────────
        /// <summary>RugCheck normalised risk score 0-100 (higher = worse). -1 if unknown.</summary>
        public int RugCheckScore { get; set; } = -1;
        /// <summary>Named risk items reported by the provider(s), e.g. "Freeze Authority still enabled".</summary>
        public List<string> ProviderRisks { get; set; } = new();

        /// <summary>Number of provider risks rated danger/severe (informational items excluded).</summary>
        public int DangerRiskCount { get; set; }

        /// <summary>Provider has already marked this token as rugged.</summary>
        public bool IsRugged { get; set; }

        /// <summary>Insider wallet networks detected by the provider (RugCheck graph analysis). -1 if unknown.</summary>
        public int InsiderNetworkCount { get; set; } = -1;

        /// <summary>Total holder count reported by the provider. -1 if unknown.</summary>
        public int TotalHolders { get; set; } = -1;

        /// <summary>Hard fraud proof — block unconditionally.</summary>
        public bool IsCriticalThreat =>
            IsHoneypot || OwnerCanChangeBalance || SellTaxPct >= 99 || IsRugged;

        public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    }
}
