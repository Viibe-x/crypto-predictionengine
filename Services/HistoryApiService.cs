using System.Data;
using System.Text.Json;
using DexScannerBot.Models;
using Npgsql;

namespace DexScannerBot.Services
{
    /// <summary>
    /// REST API service for querying historical scan data.
    /// Operates against PostgreSQL if available, or seamlessly falls back to the in-memory
    /// store so all endpoints work without requiring an external database.
    /// </summary>
    public class HistoryApiService : IDisposable
    {
        private readonly string _connectionString;
        private NpgsqlConnection? _connection;
        private bool _initialized;
        private bool _isPostgresAvailable;

        public HistoryApiService(AppConfig config)
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
                _isPostgresAvailable = true;
                _initialized = true;
            }
            catch
            {
                _isPostgresAvailable = false;
                _initialized = true;
            }
        }

        public bool IsReady => _initialized;

        public async Task<List<TokenSearchResult>> SearchTokensAsync(
            string? query = null,
            string? chain = null,
            double? minScore = null,
            double? maxScore = null,
            string? profitTier = null,
            int limit = 50,
            int offset = 0,
            CancellationToken ct = default)
        {
            if (_isPostgresAvailable && _connection?.State == ConnectionState.Open)
            {
                try
                {
                    return await SearchTokensPostgresAsync(query, chain, minScore, maxScore, profitTier, limit, offset, ct);
                }
                catch { }
            }

            // In-Memory Fallback
            var tokens = DatabaseService.MemoryTokens.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(chain))
                tokens = tokens.Where(t => t.Chain.Equals(chain, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query))
                tokens = tokens.Where(t => t.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                           t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                           t.Address.Contains(query, StringComparison.OrdinalIgnoreCase));

            var results = new List<TokenSearchResult>();
            foreach (var tok in tokens)
            {
                var analysis = DatabaseService.MemoryAnalyses.Where(a => a.TokenId == tok.Id).OrderByDescending(a => a.AnalyzedAt).FirstOrDefault();
                var snap = DatabaseService.MemorySnapshots.Where(s => s.TokenId == tok.Id).OrderByDescending(s => s.SnapshotAt).FirstOrDefault();

                if (minScore.HasValue && (analysis?.TotalScore ?? 0) < minScore.Value) continue;
                if (maxScore.HasValue && (analysis?.TotalScore ?? 0) > maxScore.Value) continue;
                if (!string.IsNullOrWhiteSpace(profitTier) && analysis?.ProfitTier.Contains(profitTier, StringComparison.OrdinalIgnoreCase) != true) continue;

                results.Add(new TokenSearchResult
                {
                    Id = tok.Id,
                    Address = tok.Address,
                    Chain = tok.Chain,
                    Symbol = tok.Symbol,
                    Name = tok.Name,
                    TotalScore = analysis?.TotalScore ?? 0,
                    ProfitTier = analysis?.ProfitTier ?? "",
                    Verdict = analysis?.Verdict ?? "",
                    RiskLevel = analysis?.RiskLevel ?? "",
                    RugRiskScore = analysis?.RugRiskScore ?? 0,
                    EntrySignal = analysis?.EntrySignal ?? "",
                    EstimatedMultiplier = analysis?.EstimatedMultiplier ?? 0,
                    ProfitConfidence = analysis?.ProfitConfidence ?? 0,
                    AnalyzedAt = analysis?.AnalyzedAt ?? tok.CreatedAt,
                    PriceUsd = snap?.PriceUsd ?? "",
                    MarketCap = snap?.MarketCap ?? 0,
                    LiquidityUsd = snap?.LiquidityUsd ?? 0,
                    Vol24h = snap?.Vol24h ?? 0
                });
            }

            return results.OrderByDescending(r => r.AnalyzedAt).Skip(offset).Take(limit).ToList();
        }

        private async Task<List<TokenSearchResult>> SearchTokensPostgresAsync(
            string? query, string? chain, double? minScore, double? maxScore, string? profitTier, int limit, int offset, CancellationToken ct)
        {
            var sql = @"
                SELECT DISTINCT ON (t.address, t.chain)
                    t.id, t.address, t.chain, t.symbol, t.name,
                    a.total_score, a.profit_tier, a.verdict, a.risk_level,
                    a.rug_risk_score, a.entry_signal, a.estimated_multiplier,
                    a.profit_confidence, a.analyzed_at,
                    ps.price_usd, ps.market_cap, ps.liquidity_usd, ps.vol_24h
                FROM tokens t
                LEFT JOIN analyses a ON t.id = a.token_id
                LEFT JOIN pair_snapshots ps ON t.id = ps.token_id
                WHERE 1=1";

            var parameters = new List<NpgsqlParameter>();

            if (!string.IsNullOrWhiteSpace(query))
            {
                sql += " AND (t.symbol ILIKE @q OR t.name ILIKE @q OR t.address ILIKE @q)";
                parameters.Add(new NpgsqlParameter("q", $"%{query}%"));
            }

            if (!string.IsNullOrWhiteSpace(chain))
            {
                sql += " AND t.chain = @chain";
                parameters.Add(new NpgsqlParameter("chain", chain));
            }

            if (minScore.HasValue)
            {
                sql += " AND a.total_score >= @minScore";
                parameters.Add(new NpgsqlParameter("minScore", minScore.Value));
            }

            if (maxScore.HasValue)
            {
                sql += " AND a.total_score <= @maxScore";
                parameters.Add(new NpgsqlParameter("maxScore", maxScore.Value));
            }

            if (!string.IsNullOrWhiteSpace(profitTier))
            {
                sql += " AND a.profit_tier ILIKE @tier";
                parameters.Add(new NpgsqlParameter("tier", $"%{profitTier}%"));
            }

            sql += " ORDER BY t.address, t.chain, a.analyzed_at DESC LIMIT @limit OFFSET @offset";
            parameters.Add(new NpgsqlParameter("limit", limit));
            parameters.Add(new NpgsqlParameter("offset", offset));

            using var cmd = new NpgsqlCommand(sql, _connection!);
            cmd.Parameters.AddRange(parameters.ToArray());

            var results = new List<TokenSearchResult>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new TokenSearchResult
                {
                    Id = reader.GetGuid(0),
                    Address = reader.GetString(1),
                    Chain = reader.GetString(2),
                    Symbol = reader.GetString(3),
                    Name = reader.GetString(4),
                    TotalScore = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                    ProfitTier = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    Verdict = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    RiskLevel = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    RugRiskScore = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                    EntrySignal = reader.IsDBNull(10) ? "" : reader.GetString(10),
                    EstimatedMultiplier = reader.IsDBNull(11) ? 0 : reader.GetDouble(11),
                    ProfitConfidence = reader.IsDBNull(12) ? 0 : reader.GetDouble(12),
                    AnalyzedAt = reader.IsDBNull(13) ? DateTime.MinValue : reader.GetDateTime(13),
                    PriceUsd = reader.IsDBNull(14) ? "" : reader.GetString(14),
                    MarketCap = reader.IsDBNull(15) ? 0 : reader.GetDouble(15),
                    LiquidityUsd = reader.IsDBNull(16) ? 0 : reader.GetDouble(16),
                    Vol24h = reader.IsDBNull(17) ? 0 : reader.GetDouble(17)
                });
            }

            return results;
        }

        public async Task<List<TokenSearchResult>> GetTopTokensAsync(
            string period = "24h",
            int limit = 50,
            CancellationToken ct = default)
        {
            if (_isPostgresAvailable && _connection?.State == ConnectionState.Open)
            {
                try
                {
                    var interval = period.ToLower() switch
                    {
                        "7d" => "7 days",
                        "30d" => "30 days",
                        "1h" => "1 hour",
                        _ => "24 hours"
                    };

                    var sql = @"
                        SELECT DISTINCT ON (t.address, t.chain)
                            t.id, t.address, t.chain, t.symbol, t.name,
                            a.total_score, a.profit_tier, a.verdict, a.risk_level,
                            a.rug_risk_score, a.entry_signal, a.estimated_multiplier,
                            a.profit_confidence, a.analyzed_at,
                            ps.price_usd, ps.market_cap, ps.liquidity_usd, ps.vol_24h
                        FROM tokens t
                        INNER JOIN analyses a ON t.id = a.token_id
                        LEFT JOIN pair_snapshots ps ON t.id = ps.token_id
                        WHERE a.analyzed_at > now() - @interval::interval
                        ORDER BY t.address, t.chain, a.total_score DESC
                        LIMIT @limit";

                    using var cmd = new NpgsqlCommand(sql, _connection!);
                    cmd.Parameters.AddWithValue("interval", interval);
                    cmd.Parameters.AddWithValue("limit", limit);

                    var results = new List<TokenSearchResult>();
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        results.Add(new TokenSearchResult
                        {
                            Id = reader.GetGuid(0),
                            Address = reader.GetString(1),
                            Chain = reader.GetString(2),
                            Symbol = reader.GetString(3),
                            Name = reader.GetString(4),
                            TotalScore = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                            ProfitTier = reader.IsDBNull(6) ? "" : reader.GetString(6),
                            Verdict = reader.IsDBNull(7) ? "" : reader.GetString(7),
                            RiskLevel = reader.IsDBNull(8) ? "" : reader.GetString(8),
                            RugRiskScore = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                            EntrySignal = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            EstimatedMultiplier = reader.IsDBNull(11) ? 0 : reader.GetDouble(11),
                            ProfitConfidence = reader.IsDBNull(12) ? 0 : reader.GetDouble(12),
                            AnalyzedAt = reader.IsDBNull(13) ? DateTime.MinValue : reader.GetDateTime(13),
                            PriceUsd = reader.IsDBNull(14) ? "" : reader.GetString(14),
                            MarketCap = reader.IsDBNull(15) ? 0 : reader.GetDouble(15),
                            LiquidityUsd = reader.IsDBNull(16) ? 0 : reader.GetDouble(16),
                            Vol24h = reader.IsDBNull(17) ? 0 : reader.GetDouble(17)
                        });
                    }
                    return results;
                }
                catch { }
            }

            // In-memory top
            return await SearchTokensAsync(limit: limit, ct: ct);
        }

        public async Task<TokenHistoryResult?> GetTokenHistoryAsync(
            string address,
            string chain = "solana",
            CancellationToken ct = default)
        {
            string key = $"{chain}:{address}".ToLowerInvariant();
            if (!DatabaseService.MemoryTokens.TryGetValue(key, out var tok))
            {
                return null;
            }

            var result = new TokenHistoryResult
            {
                Address = tok.Address,
                Chain = tok.Chain,
                Symbol = tok.Symbol,
                Name = tok.Name,
                CreatedAt = tok.CreatedAt
            };

            var analyses = DatabaseService.MemoryAnalyses.Where(a => a.TokenId == tok.Id).OrderByDescending(a => a.AnalyzedAt);
            foreach (var a in analyses)
            {
                result.Analyses.Add(new AnalysisHistoryEntry
                {
                    TotalScore = a.TotalScore,
                    ProfitTier = a.ProfitTier,
                    Verdict = a.Verdict,
                    RiskLevel = a.RiskLevel,
                    RugRiskScore = a.RugRiskScore,
                    EntrySignal = a.EntrySignal,
                    EstimatedMultiplier = a.EstimatedMultiplier,
                    ProfitConfidence = a.ProfitConfidence,
                    BullishSignals = a.BullishSignals,
                    WarningSignals = a.WarningSignals,
                    ProfitReasons = a.ProfitReasons,
                    AnalyzedAt = a.AnalyzedAt
                });
            }

            var snaps = DatabaseService.MemorySnapshots.Where(s => s.TokenId == tok.Id).OrderByDescending(s => s.SnapshotAt).Take(100);
            foreach (var s in snaps)
            {
                result.Snapshots.Add(new PriceSnapshot
                {
                    PriceUsd = s.PriceUsd,
                    MarketCap = s.MarketCap,
                    LiquidityUsd = s.LiquidityUsd,
                    Vol24h = s.Vol24h,
                    Change5m = s.PriceChange5m,
                    Change1h = s.PriceChange1h,
                    Change24h = s.PriceChange24h,
                    SnapshotAt = s.SnapshotAt
                });
            }

            return result;
        }

        public async Task<List<TradeHistoryEntry>> GetTradeHistoryAsync(
            string? status = null,
            DateTime? from = null,
            DateTime? to = null,
            int limit = 100,
            CancellationToken ct = default)
        {
            var trades = DatabaseService.MemoryTrades.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(status))
                trades = trades.Where(t => t.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase));
            if (from.HasValue)
                trades = trades.Where(t => (t.ExecutedAt ?? t.CreatedAt) >= from.Value);
            if (to.HasValue)
                trades = trades.Where(t => (t.ExecutedAt ?? t.CreatedAt) <= to.Value);

            return trades.OrderByDescending(t => t.CreatedAt).Take(limit).Select(t => new TradeHistoryEntry
            {
                Id = t.Id,
                Symbol = t.TokenAddress,
                Address = t.TokenAddress,
                Side = t.Side.ToString(),
                SizeUsd = t.SizeUsd,
                SizeSol = t.SizeSol,
                EntryPrice = t.EntryPrice,
                ExitPrice = t.ExitPrice,
                RealizedPnl = t.RealizedPnl,
                RealizedPnlPct = t.RealizedPnlPct,
                TxHash = t.TxHash ?? "",
                Status = t.Status.ToString(),
                ExecutedAt = t.ExecutedAt ?? t.CreatedAt,
                CreatedAt = t.CreatedAt
            }).ToList();
        }

        public async Task<AnalyticsSummary> GetAnalyticsSummaryAsync(
            string period = "24h",
            CancellationToken ct = default)
        {
            var summary = new AnalyticsSummary { Period = period };
            var analyses = DatabaseService.MemoryAnalyses.ToList();
            var trades = DatabaseService.MemoryTrades.ToList();

            summary.TotalAnalyses = analyses.Count;
            summary.UniqueTokens = DatabaseService.MemoryTokens.Count;
            if (analyses.Count > 0)
            {
                summary.AverageScore = analyses.Average(a => a.TotalScore);
                summary.MaxScore = analyses.Max(a => a.TotalScore);
                summary.AverageRugRisk = analyses.Average(a => a.RugRiskScore);

                foreach (var group in analyses.GroupBy(a => a.ProfitTier))
                {
                    summary.ProfitTierBreakdown[group.Key] = group.Count();
                }
            }

            summary.TotalTrades = trades.Count;
            var wins = trades.Where(t => t.RealizedPnl > 0).ToList();
            summary.WinningTrades = wins.Count;
            summary.TotalPnl = trades.Sum(t => t.RealizedPnl);
            summary.AveragePnlPct = trades.Count > 0 ? trades.Average(t => t.RealizedPnlPct) : 0;
            summary.BestTrade = trades.Count > 0 ? trades.Max(t => t.RealizedPnl) : 0;
            summary.WorstTrade = trades.Count > 0 ? trades.Min(t => t.RealizedPnl) : 0;
            summary.WinRate = trades.Count > 0 ? (double)wins.Count / trades.Count * 100 : 0;

            return summary;
        }

        public async Task<List<PerformanceDataPoint>> GetPerformanceOverTimeAsync(
            string period = "7d",
            CancellationToken ct = default)
        {
            var trades = DatabaseService.MemoryTrades.Where(t => t.ExecutedAt != null).ToList();
            return trades.GroupBy(t => new DateTime(t.ExecutedAt!.Value.Year, t.ExecutedAt.Value.Month, t.ExecutedAt.Value.Day, t.ExecutedAt.Value.Hour, 0, 0, DateTimeKind.Utc))
                         .Select(g => new PerformanceDataPoint
                         {
                             Timestamp = g.Key,
                             Trades = g.Count(),
                             Pnl = g.Sum(t => t.RealizedPnl),
                             AveragePnlPct = g.Average(t => t.RealizedPnlPct)
                         }).OrderBy(p => p.Timestamp).ToList();
        }

        public void Dispose()
        {
            _connection?.Dispose();
        }
    }

    // API Response Models
    public class TokenSearchResult
    {
        public Guid Id { get; set; }
        public string Address { get; set; } = "";
        public string Chain { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public double TotalScore { get; set; }
        public string ProfitTier { get; set; } = "";
        public string Verdict { get; set; } = "";
        public string RiskLevel { get; set; } = "";
        public int RugRiskScore { get; set; }
        public string EntrySignal { get; set; } = "";
        public double EstimatedMultiplier { get; set; }
        public double ProfitConfidence { get; set; }
        public DateTime AnalyzedAt { get; set; }
        public string PriceUsd { get; set; } = "";
        public double MarketCap { get; set; }
        public double LiquidityUsd { get; set; }
        public double Vol24h { get; set; }
    }

    public class TokenHistoryResult
    {
        public string Address { get; set; } = "";
        public string Chain { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public List<AnalysisHistoryEntry> Analyses { get; set; } = new();
        public List<PriceSnapshot> Snapshots { get; set; } = new();
    }

    public class AnalysisHistoryEntry
    {
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
        public DateTime AnalyzedAt { get; set; }
    }

    public class PriceSnapshot
    {
        public string PriceUsd { get; set; } = "";
        public double MarketCap { get; set; }
        public double LiquidityUsd { get; set; }
        public double Vol24h { get; set; }
        public double Change5m { get; set; }
        public double Change1h { get; set; }
        public double Change24h { get; set; }
        public DateTime SnapshotAt { get; set; }
    }

    public class TradeHistoryEntry
    {
        public Guid Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Address { get; set; } = "";
        public string Side { get; set; } = "";
        public double SizeUsd { get; set; }
        public double SizeSol { get; set; }
        public string EntryPrice { get; set; } = "";
        public string ExitPrice { get; set; } = "";
        public double RealizedPnl { get; set; }
        public double RealizedPnlPct { get; set; }
        public string TxHash { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime ExecutedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class AnalyticsSummary
    {
        public string Period { get; set; } = "";
        public int TotalAnalyses { get; set; }
        public int UniqueTokens { get; set; }
        public double AverageScore { get; set; }
        public double MaxScore { get; set; }
        public double AverageRugRisk { get; set; }
        public Dictionary<string, int> ProfitTierBreakdown { get; set; } = new();
        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public double WinRate { get; set; }
        public double TotalPnl { get; set; }
        public double AveragePnlPct { get; set; }
        public double BestTrade { get; set; }
        public double WorstTrade { get; set; }
    }

    public class PerformanceDataPoint
    {
        public DateTime Timestamp { get; set; }
        public int Trades { get; set; }
        public double Pnl { get; set; }
        public double AveragePnlPct { get; set; }
    }
}
