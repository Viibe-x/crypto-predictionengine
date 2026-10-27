using System.Collections.Concurrent;
using System.Text.Json;
using DexScannerBot.Models;
using DexScannerBot.Services;

namespace DexScannerBot.Core
{
    /// <summary>
    /// Paper trading engine that simulates real trading without executing on-chain transactions.
    /// Validates the scoring engine by tracking what WOULD have happened if trades were real.
    /// 
    /// Features:
    /// - Simulates entry/exit with realistic slippage and fees
    /// - Tracks win rate, avg PnL, Sharpe ratio, max drawdown
    /// - Compares predicted vs actual outcomes
    /// - Exports results for analysis
    /// </summary>
    public class PaperTradingEngine
    {
        private readonly AppConfig _config;
        private readonly ConcurrentDictionary<string, PaperTrade> _openTrades = new();
        private readonly List<PaperTrade> _closedTrades = new();
        private readonly object _tradeLock = new();
        private readonly double _slippagePct;
        private readonly double _tradingFeePct;
        private double _initialBalance;
        private double _currentBalance;
        private double _peakBalance;
        private double _maxDrawdown;

        public PaperTradingEngine(AppConfig config)
        {
            _config = config;
            _slippagePct = 0.005; // fallback when no execution plan is available
            _tradingFeePct = 0.003; // fallback pool fee
            _initialBalance = 1000.0; // Start with $1000 paper balance
            _currentBalance = _initialBalance;
            _peakBalance = _initialBalance;
            _maxDrawdown = 0;
        }

        public double CurrentBalance => _currentBalance;
        public double InitialBalance => _initialBalance;
        public double TotalPnl => _currentBalance - _initialBalance;
        public double TotalPnlPct => (_currentBalance - _initialBalance) / _initialBalance * 100;
        public double MaxDrawdown => _maxDrawdown;
        public int OpenTradeCount => _openTrades.Count;
        public int ClosedTradeCount
        {
            get { lock (_tradeLock) return _closedTrades.Count; }
        }

        /// <summary>Trades closed since UTC midnight.</summary>
        public int TradesToday
        {
            get { lock (_tradeLock) return _closedTrades.Count(t => t.ClosedAt?.Date == DateTime.UtcNow.Date); }
        }

        /// <summary>Realized PnL since UTC midnight, USD.</summary>
        public double PnlToday
        {
            get { lock (_tradeLock) return _closedTrades.Where(t => t.ClosedAt?.Date == DateTime.UtcNow.Date).Sum(t => t.RealizedPnl); }
        }

        /// <summary>
        /// Opens a paper trade based on a surfaced token analysis.
        /// </summary>
        public PaperTrade? OpenTrade(TokenAnalysis analysis)
        {
            double entryPrice = DexJson.ParsePrice(analysis.PriceUsd);
            if (entryPrice <= 0 || string.IsNullOrEmpty(analysis.Address)) return null;

            // Only buy what the engine green-lights right now.
            if (analysis.EntryDecision != "INVEST_NOW") return null;

            // One open trade per token — a repeat signal used to overwrite the open trade
            // and deduct the balance twice.
            if (_openTrades.ContainsKey(analysis.Address)) return null;

            var plan = analysis.Prediction?.Plan;
            double p = analysis.Prediction?.SpikeProbability ?? analysis.CalibratedScore / 100.0;

            // Size by probability, never more than 10% of balance.
            double positionSize = p >= 0.75 ? 400 : p >= 0.6 ? 200 : 100;
            positionSize = Math.Min(positionSize, _currentBalance * 0.10);
            if (positionSize < 10) return null; // Minimum $10

            // Simulate what a wallet actually pays: price impact for this size on the pool,
            // plus pool fee and any buy tax (from the engine's execution plan).
            double quoteReserve = Math.Max(analysis.LiquidityUsd / 2.0, 1);
            double impact = plan != null ? positionSize / quoteReserve : _slippagePct;
            double feePct = plan != null ? (plan.DexFeePct + plan.BuyTaxPct) / 100.0 : _tradingFeePct;
            double slippedEntry = entryPrice * (1 + impact);
            double fees = positionSize * feePct;
            double netPosition = positionSize - fees;

            double stopPct = plan?.StopLossPct ?? -_config.Position.StopLossPct * 100;
            double tpPct = plan?.TakeProfit1Pct ?? (_config.Position.PartialTp1Mult - 1) * 100;

            var trade = new PaperTrade
            {
                Id = Guid.NewGuid(),
                Symbol = analysis.Symbol,
                Address = analysis.Address,
                Chain = analysis.Chain,
                EntryPrice = slippedEntry,
                RawEntryPrice = entryPrice,
                SizeUsd = netPosition,
                ScoreAtEntry = analysis.TotalScore,
                SpikeProbability = p,
                ProfitTier = analysis.ProfitTier,
                EntrySignal = analysis.EntrySignal,
                EstimatedMultiplier = analysis.EstimatedMultiplier,
                RugRiskScore = analysis.RugRiskScore,
                LiquidityUsd = analysis.LiquidityUsd,
                ExitCostPct = plan != null ? plan.DexFeePct + plan.SellTaxPct : _tradingFeePct * 100,
                OpenedAt = DateTime.UtcNow,
                Status = PaperTradeStatus.Open,
                HighestPrice = slippedEntry,
                LowestPrice = slippedEntry,
                StopLossPrice = slippedEntry * (1 - stopPct / 100.0),
                TakeProfitPrice = slippedEntry * (1 + tpPct / 100.0),
                FeesPaid = fees
            };

            if (!_openTrades.TryAdd(analysis.Address, trade)) return null;
            _currentBalance -= positionSize;

            Console.WriteLine($"[PAPER] 📈 OPEN {analysis.Symbol} @ ${slippedEntry:G6} (impact {impact * 100:F2}%) | Size: ${netPosition:F2} | Spike p={p * 100:F0}% | SL: ${trade.StopLossPrice:G6} TP: ${trade.TakeProfitPrice:G6}");

            return trade;
        }

        /// <summary>
        /// Updates all open trades with current market price and checks exit conditions.
        /// </summary>
        public void UpdateTrades(Dictionary<string, double> currentPrices)
        {
            var toClose = new List<string>();

            foreach (var kvp in _openTrades)
            {
                var trade = kvp.Value;
                if (!currentPrices.TryGetValue(kvp.Key, out var currentPrice))
                    continue;

                trade.CurrentPrice = currentPrice;
                trade.UnrealizedPnlPct = (currentPrice - trade.EntryPrice) / trade.EntryPrice * 100;
                trade.UnrealizedPnl = trade.SizeUsd * trade.UnrealizedPnlPct / 100;
                trade.HighestPrice = Math.Max(trade.HighestPrice, currentPrice);
                trade.LowestPrice = Math.Min(trade.LowestPrice, currentPrice);

                // Update trailing stop if in profit
                if (trade.UnrealizedPnlPct > 0)
                {
                    var newStopPrice = currentPrice * (1 + _config.Position.TrailingStopPct);
                    if (newStopPrice > trade.StopLossPrice)
                    {
                        trade.StopLossPrice = newStopPrice;
                    }
                }

                // Check stop loss
                if (currentPrice <= trade.StopLossPrice)
                {
                    toClose.Add(kvp.Key);
                    continue;
                }

                // Check take profit
                if (currentPrice >= trade.TakeProfitPrice)
                {
                    toClose.Add(kvp.Key);
                    continue;
                }

                // Check max hold time
                var held = DateTime.UtcNow - trade.OpenedAt;
                if (held.TotalHours >= _config.Position.MaxHoldHoursAuto)
                {
                    toClose.Add(kvp.Key);
                    continue;
                }
            }

            foreach (var address in toClose)
            {
                if (_openTrades.TryGetValue(address, out var trade))
                {
                    CloseTrade(trade, currentPrices.GetValueOrDefault(address, trade.CurrentPrice), "exit_signal");
                }
            }

            // Update drawdown
            var totalValue = _currentBalance + _openTrades.Values.Sum(t => t.SizeUsd + t.UnrealizedPnl);
            if (totalValue > _peakBalance) _peakBalance = totalValue;
            var drawdown = (_peakBalance - totalValue) / _peakBalance * 100;
            if (drawdown > _maxDrawdown) _maxDrawdown = drawdown;
        }

        /// <summary>
        /// Closes a paper trade.
        /// </summary>
        public void CloseTrade(PaperTrade trade, double exitPrice, string reason)
        {
            lock (_tradeLock)
            {
                // Exit pays price impact on the same pool plus pool fee / sell tax.
                double exitImpact = trade.LiquidityUsd > 0 ? trade.SizeUsd / Math.Max(trade.LiquidityUsd / 2.0, 1) : _slippagePct;
                double slippedExit = exitPrice * (1 - Math.Min(exitImpact, 0.5));
                double exitFees = trade.SizeUsd * (trade.ExitCostPct / 100.0);
                
                trade.ExitPrice = slippedExit;
                trade.RawExitPrice = exitPrice;
                trade.UnrealizedPnlPct = (slippedExit - trade.EntryPrice) / trade.EntryPrice * 100;
                trade.UnrealizedPnl = trade.SizeUsd * trade.UnrealizedPnlPct / 100 - exitFees;
                trade.RealizedPnl = trade.UnrealizedPnl;
                trade.RealizedPnlPct = trade.UnrealizedPnlPct;
                trade.ClosedAt = DateTime.UtcNow;
                trade.CloseReason = reason;
                trade.Status = trade.RealizedPnl >= 0 ? PaperTradeStatus.Won : PaperTradeStatus.Lost;
                trade.FeesPaid += exitFees;

                _currentBalance += trade.SizeUsd + trade.RealizedPnl;
                _openTrades.TryRemove(trade.Address, out _);
                _closedTrades.Add(trade);

                var emoji = trade.Status == PaperTradeStatus.Won ? "✅" : "❌";
                Console.WriteLine($"[PAPER] {emoji} CLOSE {trade.Symbol} @ ${slippedExit:G6} | PnL: ${trade.RealizedPnl:F2} ({trade.RealizedPnlPct:F1}%) | Reason: {reason} | Balance: ${_currentBalance:F2}");
            }
        }

        /// <summary>
        /// Gets comprehensive performance statistics.
        /// </summary>
        public PaperTradingStats GetStats()
        {
            lock (_tradeLock)
            {
                var stats = new PaperTradingStats
                {
                    InitialBalance = _initialBalance,
                    CurrentBalance = _currentBalance,
                    TotalPnl = TotalPnl,
                    TotalPnlPct = TotalPnlPct,
                    MaxDrawdown = _maxDrawdown,
                    OpenTrades = _openTrades.Count,
                    ClosedTrades = _closedTrades.Count
                };

                if (_closedTrades.Count > 0)
                {
                    var wins = _closedTrades.Where(t => t.Status == PaperTradeStatus.Won).ToList();
                    var losses = _closedTrades.Where(t => t.Status == PaperTradeStatus.Lost).ToList();

                    stats.WinningTrades = wins.Count;
                    stats.LosingTrades = losses.Count;
                    stats.WinRate = (double)wins.Count / _closedTrades.Count * 100;
                    stats.AveragePnl = _closedTrades.Average(t => t.RealizedPnl);
                    stats.AveragePnlPct = _closedTrades.Average(t => t.RealizedPnlPct);
                    stats.AverageWinPct = wins.Count > 0 ? wins.Average(t => t.RealizedPnlPct) : 0;
                    stats.AverageLossPct = losses.Count > 0 ? losses.Average(t => t.RealizedPnlPct) : 0;
                    stats.BestTrade = _closedTrades.Max(t => t.RealizedPnl);
                    stats.WorstTrade = _closedTrades.Min(t => t.RealizedPnl);
                    stats.ProfitFactor = losses.Sum(t => Math.Abs(t.RealizedPnl)) > 0
                        ? wins.Sum(t => t.RealizedPnl) / losses.Sum(t => Math.Abs(t.RealizedPnl))
                        : (wins.Count > 0 ? 99.0 : 0);
                    stats.TotalFees = _closedTrades.Sum(t => t.FeesPaid);
                    stats.AverageHoldTime = TimeSpan.FromTicks((long)_closedTrades.Average(t => (t.ClosedAt!.Value - t.OpenedAt).Ticks));

                    // Sharpe ratio (annualized, assuming risk-free rate of 0)
                    var returns = _closedTrades.Select(t => t.RealizedPnlPct / 100.0).ToList();
                    if (returns.Count > 1)
                    {
                        var avgReturn = returns.Average();
                        var stdDev = Math.Sqrt(returns.Sum(r => Math.Pow(r - avgReturn, 2)) / (returns.Count - 1));
                        stats.SharpeRatio = stdDev > 0 ? avgReturn / stdDev * Math.Sqrt(252) : 0;
                    }

                    // Score correlation
                    var highScoreTrades = _closedTrades.Where(t => t.ScoreAtEntry >= 75).ToList();
                    var lowScoreTrades = _closedTrades.Where(t => t.ScoreAtEntry < 75).ToList();
                    stats.HighScoreWinRate = highScoreTrades.Count > 0 ? (double)highScoreTrades.Count(t => t.Status == PaperTradeStatus.Won) / highScoreTrades.Count * 100 : 0;
                    stats.LowScoreWinRate = lowScoreTrades.Count > 0 ? (double)lowScoreTrades.Count(t => t.Status == PaperTradeStatus.Won) / lowScoreTrades.Count * 100 : 0;
                }

                return stats;
            }
        }

        /// <summary>
        /// Gets all closed trades.
        /// </summary>
        public List<PaperTrade> GetClosedTrades()
        {
            lock (_tradeLock) return _closedTrades.ToList();
        }

        /// <summary>
        /// Gets all open trades.
        /// </summary>
        public List<PaperTrade> GetOpenTrades()
        {
            return _openTrades.Values.ToList();
        }

        /// <summary>
        /// Exports trading results to JSON for analysis.
        /// </summary>
        public async Task ExportResultsAsync(string path, CancellationToken ct = default)
        {
            var export = new
            {
                Stats = GetStats(),
                ClosedTrades = _closedTrades.OrderByDescending(t => t.ClosedAt).ToList(),
                OpenTrades = _openTrades.Values.ToList()
            };

            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, ct);
        }
    }

    public class PaperTrade
    {
        public Guid Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Address { get; set; } = "";
        public string Chain { get; set; } = "";
        public double EntryPrice { get; set; }
        public double RawEntryPrice { get; set; }
        public double ExitPrice { get; set; }
        public double RawExitPrice { get; set; }
        public double CurrentPrice { get; set; }
        public double SizeUsd { get; set; }
        public double ScoreAtEntry { get; set; }
        public double SpikeProbability { get; set; }
        public double LiquidityUsd { get; set; }
        public double ExitCostPct { get; set; }
        public string ProfitTier { get; set; } = "";
        public string EntrySignal { get; set; } = "";
        public double EstimatedMultiplier { get; set; }
        public int RugRiskScore { get; set; }
        public double UnrealizedPnl { get; set; }
        public double UnrealizedPnlPct { get; set; }
        public double RealizedPnl { get; set; }
        public double RealizedPnlPct { get; set; }
        public double HighestPrice { get; set; }
        public double LowestPrice { get; set; }
        public double StopLossPrice { get; set; }
        public double TakeProfitPrice { get; set; }
        public double FeesPaid { get; set; }
        public DateTime OpenedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string CloseReason { get; set; } = "";
        public PaperTradeStatus Status { get; set; }
    }

    public enum PaperTradeStatus { Open, Won, Lost }

    public class PaperTradingStats
    {
        public double InitialBalance { get; set; }
        public double CurrentBalance { get; set; }
        public double TotalPnl { get; set; }
        public double TotalPnlPct { get; set; }
        public double MaxDrawdown { get; set; }
        public int OpenTrades { get; set; }
        public int ClosedTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public double WinRate { get; set; }
        public double AveragePnl { get; set; }
        public double AveragePnlPct { get; set; }
        public double AverageWinPct { get; set; }
        public double AverageLossPct { get; set; }
        public double BestTrade { get; set; }
        public double WorstTrade { get; set; }
        public double ProfitFactor { get; set; }
        public double SharpeRatio { get; set; }
        public double TotalFees { get; set; }
        public TimeSpan AverageHoldTime { get; set; }
        public double HighScoreWinRate { get; set; }
        public double LowScoreWinRate { get; set; }
    }
}
