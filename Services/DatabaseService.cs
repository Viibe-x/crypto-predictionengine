using System.Collections.Concurrent;
using System.Data;
using DexScannerBot.Models;
using Npgsql;

namespace DexScannerBot.Services
{
    public class StoredToken
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Address { get; set; } = "";
        public string Chain { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class StoredSnapshot
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TokenId { get; set; }
        public string DexId { get; set; } = "";
        public string PairAddress { get; set; } = "";
        public string PriceUsd { get; set; } = "";
        public double MarketCap { get; set; }
        public double Fdv { get; set; }
        public double LiquidityUsd { get; set; }
        public double Vol5m { get; set; }
        public double Vol1h { get; set; }
        public double Vol6h { get; set; }
        public double Vol24h { get; set; }
        public double PriceChange5m { get; set; }
        public double PriceChange1h { get; set; }
        public double PriceChange6h { get; set; }
        public double PriceChange24h { get; set; }
        public int Buys5m { get; set; }
        public int Sells5m { get; set; }
        public int Buys1h { get; set; }
        public int Sells1h { get; set; }
        public int Buys24h { get; set; }
        public int Sells24h { get; set; }
        public DateTime SnapshotAt { get; set; } = DateTime.UtcNow;
    }

    public class StoredAnalysis
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TokenId { get; set; }
        public Guid SnapshotId { get; set; }
        public double TotalScore { get; set; }
        public string ProfitTier { get; set; } = "";
        public string Verdict { get; set; } = "";
        public string RiskLevel { get; set; } = "";
        public int RugRiskScore { get; set; }
        public string EntrySignal { get; set; } = "";
        public double EstimatedMultiplier { get; set; }
        public double ProfitConfidence { get; set; }
        public List<string> BullishSignals { get; set; } = new();
        public List<string> WarningSignals { get; set; } = new();
        public List<string> ProfitReasons { get; set; } = new();
        public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
    }

    public class DatabaseService : IDisposable
    {
        private readonly string _connectionString;
        private NpgsqlConnection? _connection;
        private bool _initialized;
        private bool _isPostgresAvailable;

        // In-Memory Storage Fallback (stores up to 5,000 recent items per collection)
        public static readonly ConcurrentDictionary<string, StoredToken> MemoryTokens = new(StringComparer.OrdinalIgnoreCase);
        public static readonly ConcurrentBag<StoredSnapshot> MemorySnapshots = new();
        public static readonly ConcurrentBag<StoredAnalysis> MemoryAnalyses = new();
        public static readonly ConcurrentBag<TradeRecord> MemoryTrades = new();

        public bool IsPostgresAvailable => _isPostgresAvailable;

        public DatabaseService(AppConfig config)
        {
            _connectionString = $"Host={config.Database.Host};Port={config.Database.Port};" +
                $"Database={config.Database.Name};Username={config.Database.User};" +
                $"Password={config.Database.Password};SSL Mode={config.Database.SslMode};" +
                $"Pooling=true;Minimum Pool Size=1;Maximum Pool Size={config.Database.PoolMax};" +
                $"Timeout=2;Command Timeout=5;";
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            if (_initialized) return;
            try
            {
                _connection = new NpgsqlConnection(_connectionString);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                await _connection.OpenAsync(linkedCts.Token);
                await CreateSchemaAsync(linkedCts.Token);
                _isPostgresAvailable = true;
                _initialized = true;
                Console.WriteLine("[DB] ✓ PostgreSQL connected & schema ready");
            }
            catch
            {
                _isPostgresAvailable = false;
                _initialized = true;
                Console.WriteLine("[DB] ✓ In-Memory storage active (PostgreSQL optional)");
            }
        }

        private async Task CreateSchemaAsync(CancellationToken ct)
        {
            if (_connection == null) return;
            var schema = @"
CREATE TABLE IF NOT EXISTS tokens (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    address TEXT NOT NULL,
    chain TEXT NOT NULL,
    symbol TEXT NOT NULL,
    name TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(address, chain)
);

CREATE TABLE IF NOT EXISTS pair_snapshots (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_id UUID REFERENCES tokens(id),
    dex_id TEXT NOT NULL,
    pair_address TEXT,
    price_usd TEXT,
    market_cap DOUBLE PRECISION,
    fdv DOUBLE PRECISION,
    liquidity_usd DOUBLE PRECISION,
    vol_5m DOUBLE PRECISION,
    vol_1h DOUBLE PRECISION,
    vol_6h DOUBLE PRECISION,
    vol_24h DOUBLE PRECISION,
    price_change_5m DOUBLE PRECISION,
    price_change_1h DOUBLE PRECISION,
    price_change_6h DOUBLE PRECISION,
    price_change_24h DOUBLE PRECISION,
    buys_5m INT, sells_5m INT,
    buys_1h INT, sells_1h INT,
    buys_24h INT, sells_24h INT,
    snapshot_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS analyses (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_id UUID REFERENCES tokens(id),
    snapshot_id UUID REFERENCES pair_snapshots(id),
    total_score DOUBLE PRECISION NOT NULL,
    profit_tier TEXT NOT NULL,
    verdict TEXT NOT NULL,
    risk_level TEXT NOT NULL,
    rug_risk_score INT NOT NULL DEFAULT 0,
    entry_signal TEXT,
    estimated_multiplier DOUBLE PRECISION,
    profit_confidence DOUBLE PRECISION,
    bullish_signals JSONB,
    warning_signals JSONB,
    profit_reasons JSONB,
    analyzed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS trades (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    token_id UUID REFERENCES tokens(id),
    analysis_id UUID REFERENCES analyses(id),
    side TEXT NOT NULL CHECK (side IN ('BUY', 'SELL')),
    size_sol DOUBLE PRECISION,
    size_usd DOUBLE PRECISION,
    entry_price TEXT,
    exit_price TEXT,
    realized_pnl DOUBLE PRECISION,
    realized_pnl_pct DOUBLE PRECISION,
    tx_hash TEXT,
    status TEXT NOT NULL CHECK (status IN ('PENDING', 'CONFIRMED', 'FAILED', 'CANCELLED')),
    executed_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS scan_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    ended_at TIMESTAMPTZ,
    scanned_count INT NOT NULL DEFAULT 0,
    surfaced_count INT NOT NULL DEFAULT 0,
    rejected_count INT NOT NULL DEFAULT 0,
    source TEXT
);

CREATE INDEX IF NOT EXISTS idx_tokens_search ON tokens USING gin(to_tsvector('english', symbol || ' ' || name));
CREATE INDEX IF NOT EXISTS idx_pair_snapshots_token_time ON pair_snapshots(token_id, snapshot_at DESC);
CREATE INDEX IF NOT EXISTS idx_analyses_token_time ON analyses(token_id, analyzed_at DESC);
CREATE INDEX IF NOT EXISTS idx_trades_token_time ON trades(token_id, executed_at DESC);
";

            using var cmd = new NpgsqlCommand(schema, _connection);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<Guid> GetOrCreateTokenAsync(string address, string chain, string symbol, string name, CancellationToken ct = default)
        {
            string key = $"{chain}:{address}".ToLowerInvariant();
            var memToken = MemoryTokens.GetOrAdd(key, _ => new StoredToken
            {
                Address = address,
                Chain = chain,
                Symbol = symbol,
                Name = name
            });

            if (!_isPostgresAvailable || _connection == null)
            {
                return memToken.Id;
            }

            try
            {
                const string upsert = @"
INSERT INTO tokens (address, chain, symbol, name) VALUES (@a, @c, @s, @n)
ON CONFLICT (address, chain) DO UPDATE SET symbol = EXCLUDED.symbol, name = EXCLUDED.name
RETURNING id;";

                using var cmd = new NpgsqlCommand(upsert, _connection);
                cmd.Parameters.AddWithValue("a", address);
                cmd.Parameters.AddWithValue("c", chain);
                cmd.Parameters.AddWithValue("s", symbol);
                cmd.Parameters.AddWithValue("n", name);
                var result = await cmd.ExecuteScalarAsync(ct);
                return (Guid)result!;
            }
            catch
            {
                return memToken.Id;
            }
        }

        public async Task<Guid> InsertSnapshotAsync(Guid tokenId, DexPair pair, CancellationToken ct = default)
        {
            var snap = new StoredSnapshot
            {
                TokenId = tokenId,
                DexId = pair.DexId ?? "",
                PairAddress = pair.PairAddress ?? "",
                PriceUsd = pair.PriceUsd ?? "",
                MarketCap = pair.MarketCap,
                Fdv = pair.Fdv,
                LiquidityUsd = pair.Liquidity?.Usd ?? 0,
                Vol5m = pair.Volume?.M5 ?? 0,
                Vol1h = pair.Volume?.H1 ?? 0,
                Vol6h = pair.Volume?.H6 ?? 0,
                Vol24h = pair.Volume?.H24 ?? 0,
                PriceChange5m = pair.PriceChange?.M5 ?? 0,
                PriceChange1h = pair.PriceChange?.H1 ?? 0,
                PriceChange6h = pair.PriceChange?.H6 ?? 0,
                PriceChange24h = pair.PriceChange?.H24 ?? 0,
                Buys5m = pair.Txns?.M5?.Buys ?? 0,
                Sells5m = pair.Txns?.M5?.Sells ?? 0,
                Buys1h = pair.Txns?.H1?.Buys ?? 0,
                Sells1h = pair.Txns?.H1?.Sells ?? 0,
                Buys24h = pair.Txns?.H24?.Buys ?? 0,
                Sells24h = pair.Txns?.H24?.Sells ?? 0
            };
            MemorySnapshots.Add(snap);

            if (!_isPostgresAvailable || _connection == null)
            {
                return snap.Id;
            }

            try
            {
                const string sql = @"
INSERT INTO pair_snapshots (token_id, dex_id, pair_address, price_usd, market_cap, fdv, liquidity_usd,
    vol_5m, vol_1h, vol_6h, vol_24h, price_change_5m, price_change_1h, price_change_6h, price_change_24h,
    buys_5m, sells_5m, buys_1h, sells_1h, buys_24h, sells_24h)
VALUES (@tid, @dex, @pa, @p, @mc, @fdv, @liq, @v5, @v1, @v6, @v24, @c5, @c1, @c6, @c24, @b5, @s5, @b1, @s1, @b24, @s24)
RETURNING id;";

                using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("tid", tokenId);
                cmd.Parameters.AddWithValue("dex", pair.DexId ?? "");
                cmd.Parameters.AddWithValue("pa", pair.PairAddress ?? "");
                cmd.Parameters.AddWithValue("p", pair.PriceUsd ?? "");
                cmd.Parameters.AddWithValue("mc", pair.MarketCap);
                cmd.Parameters.AddWithValue("fdv", pair.Fdv);
                cmd.Parameters.AddWithValue("liq", pair.Liquidity?.Usd ?? 0);
                cmd.Parameters.AddWithValue("v5", pair.Volume?.M5 ?? 0);
                cmd.Parameters.AddWithValue("v1", pair.Volume?.H1 ?? 0);
                cmd.Parameters.AddWithValue("v6", pair.Volume?.H6 ?? 0);
                cmd.Parameters.AddWithValue("v24", pair.Volume?.H24 ?? 0);
                cmd.Parameters.AddWithValue("c5", pair.PriceChange?.M5 ?? 0);
                cmd.Parameters.AddWithValue("c1", pair.PriceChange?.H1 ?? 0);
                cmd.Parameters.AddWithValue("c6", pair.PriceChange?.H6 ?? 0);
                cmd.Parameters.AddWithValue("c24", pair.PriceChange?.H24 ?? 0);
                cmd.Parameters.AddWithValue("b5", pair.Txns?.M5?.Buys ?? 0);
                cmd.Parameters.AddWithValue("s5", pair.Txns?.M5?.Sells ?? 0);
                cmd.Parameters.AddWithValue("b1", pair.Txns?.H1?.Buys ?? 0);
                cmd.Parameters.AddWithValue("s1", pair.Txns?.H1?.Sells ?? 0);
                cmd.Parameters.AddWithValue("b24", pair.Txns?.H24?.Buys ?? 0);
                cmd.Parameters.AddWithValue("s24", pair.Txns?.H24?.Sells ?? 0);
                var result = await cmd.ExecuteScalarAsync(ct);
                return (Guid)result!;
            }
            catch
            {
                return snap.Id;
            }
        }

        public async Task InsertAnalysisAsync(Guid tokenId, Guid snapshotId, TokenAnalysis analysis, CancellationToken ct = default)
        {
            var stored = new StoredAnalysis
            {
                TokenId = tokenId,
                SnapshotId = snapshotId,
                TotalScore = analysis.TotalScore,
                ProfitTier = analysis.ProfitTier,
                Verdict = analysis.Verdict,
                RiskLevel = analysis.RiskLevel,
                RugRiskScore = analysis.RugRiskScore,
                EntrySignal = analysis.EntrySignal,
                EstimatedMultiplier = analysis.EstimatedMultiplier,
                ProfitConfidence = analysis.ProfitConfidence,
                BullishSignals = new List<string>(analysis.BullishSignals),
                WarningSignals = new List<string>(analysis.WarningSignals),
                ProfitReasons = new List<string>(analysis.ProfitReasons),
                AnalyzedAt = DateTime.UtcNow
            };
            MemoryAnalyses.Add(stored);

            if (!_isPostgresAvailable || _connection == null) return;

            try
            {
                const string sql = @"
INSERT INTO analyses (token_id, snapshot_id, total_score, profit_tier, verdict, risk_level, rug_risk_score,
    entry_signal, estimated_multiplier, profit_confidence, bullish_signals, warning_signals, profit_reasons)
VALUES (@tid, @sid, @ts, @pt, @v, @rl, @rs, @es, @em, @pc, @bs, @ws, @pr);";

                using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("tid", tokenId);
                cmd.Parameters.AddWithValue("sid", snapshotId);
                cmd.Parameters.AddWithValue("ts", analysis.TotalScore);
                cmd.Parameters.AddWithValue("pt", analysis.ProfitTier);
                cmd.Parameters.AddWithValue("v", analysis.Verdict);
                cmd.Parameters.AddWithValue("rl", analysis.RiskLevel);
                cmd.Parameters.AddWithValue("rs", analysis.RugRiskScore);
                cmd.Parameters.AddWithValue("es", analysis.EntrySignal);
                cmd.Parameters.AddWithValue("em", analysis.EstimatedMultiplier);
                cmd.Parameters.AddWithValue("pc", analysis.ProfitConfidence);
                cmd.Parameters.AddWithValue("bs", NpgsqlTypes.NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(analysis.BullishSignals));
                cmd.Parameters.AddWithValue("ws", NpgsqlTypes.NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(analysis.WarningSignals));
                cmd.Parameters.AddWithValue("pr", NpgsqlTypes.NpgsqlDbType.Jsonb, System.Text.Json.JsonSerializer.Serialize(analysis.ProfitReasons));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { }
        }

        public async Task InsertTradeAsync(Guid tokenId, Guid? analysisId, TradeRecord trade, CancellationToken ct = default)
        {
            MemoryTrades.Add(trade);
            if (!_isPostgresAvailable || _connection == null) return;

            try
            {
                const string sql = @"
INSERT INTO trades (token_id, analysis_id, side, size_sol, size_usd, entry_price, exit_price,
    realized_pnl, realized_pnl_pct, tx_hash, status, executed_at)
VALUES (@tid, @aid, @s, @ssol, @susd, @ep, @xp, @pnl, @pnlp, @tx, @st, @ea);";

                using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("tid", tokenId);
                cmd.Parameters.AddWithValue("aid", analysisId ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("s", trade.Side.ToString());
                cmd.Parameters.AddWithValue("ssol", trade.SizeSol);
                cmd.Parameters.AddWithValue("susd", trade.SizeUsd);
                cmd.Parameters.AddWithValue("ep", trade.EntryPrice);
                cmd.Parameters.AddWithValue("xp", (object?)trade.ExitPrice ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pnl", trade.RealizedPnl);
                cmd.Parameters.AddWithValue("pnlp", trade.RealizedPnlPct);
                cmd.Parameters.AddWithValue("tx", (object?)trade.TxHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("st", trade.Status.ToString());
                cmd.Parameters.AddWithValue("ea", trade.ExecutedAt ?? DateTime.UtcNow);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { }
        }

        public async Task<Guid> StartScanSessionAsync(string source, CancellationToken ct = default)
        {
            if (!_isPostgresAvailable || _connection == null) return Guid.NewGuid();
            try
            {
                const string sql = "INSERT INTO scan_sessions (source) VALUES (@s) RETURNING id;";
                using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("s", source);
                var result = await cmd.ExecuteScalarAsync(ct);
                return (Guid)result!;
            }
            catch { return Guid.NewGuid(); }
        }

        public async Task EndScanSessionAsync(Guid sessionId, int scanned, int surfaced, int rejected, CancellationToken ct = default)
        {
            if (!_isPostgresAvailable || _connection == null) return;
            try
            {
                const string sql = @"UPDATE scan_sessions SET ended_at = now(), scanned_count = @sc, surfaced_count = @su, rejected_count = @re WHERE id = @id;";
                using var cmd = new NpgsqlCommand(sql, _connection);
                cmd.Parameters.AddWithValue("id", sessionId);
                cmd.Parameters.AddWithValue("sc", scanned);
                cmd.Parameters.AddWithValue("su", surfaced);
                cmd.Parameters.AddWithValue("re", rejected);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { }
        }

        public void Dispose()
        {
            _connection?.Dispose();
        }
    }
}
