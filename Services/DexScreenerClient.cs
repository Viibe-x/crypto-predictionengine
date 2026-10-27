using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DexScannerBot.Core;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    public class BoostEntry
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("chainId")] public string? ChainId { get; set; }
        [JsonPropertyName("tokenAddress")] public string? TokenAddress { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("totalAmount")] [JsonConverter(typeof(LenientDoubleConverter))] public double TotalAmount { get; set; }
    }

    public class DexScreenerClient
    {
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json = DexJson.Options;

        private readonly string[] _dataSources =
        {
            "https://api.dexscreener.com/token-boosts/latest/v1",
            "https://api.dexscreener.com/token-boosts/top/v1",
            "https://api.dexscreener.com/token-profiles/latest/v1",
        };

        // Targeted high-conviction search keywords (no single-character spam scrapers)
        private readonly string[] _searchQueries =
        {
            "solana", "pump", "raydium", "meteora", "orca", "meme", "ai", "base", "moon", "defi", "alpha"
        };
        private int _searchIndex = 0;
        private int _fetchCounter = 0;
        private const string TOKENS_URL = "https://api.dexscreener.com/latest/dex/tokens/";

        private Queue<BoostEntry> _queue = new();
        private Queue<(DexPair, BoostEntry)> _pairQueue = new();
        private int _sourceIndex = 0;
        
        // Static rate limiting to prevent workers from triggering 429 Too Many Requests
        private static readonly SemaphoreSlim _apiLock = new SemaphoreSlim(1, 1);
        private static DateTime _lastListFetch = DateTime.MinValue;
        private static DateTime _lastTokensFetch = DateTime.MinValue;

        private readonly ConcurrentDictionary<string, DateTime> _surfacedThisSession;
        private const int DEDUP_EXPIRY_SECONDS = 180; // 3 minutes
        private readonly AppConfig? _config;

        public int TotalFetched { get; private set; }
        public int TotalApiCalls { get; private set; }
        public string LastSource { get; private set; } = "—";
        public string LastError { get; private set; } = "";
        /// <summary>Measured round-trip time of the most recent DexScreener call, ms.</summary>
        public double LastLatencyMs { get; private set; }
        /// <summary>Candidates waiting to be analyzed.</summary>
        public int QueueCount => _queue.Count + _pairQueue.Count;

        private async Task<string> TimedGetAsync(string url, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var raw = await _http.GetStringAsync(url, ct);
            LastLatencyMs = sw.Elapsed.TotalMilliseconds;
            TotalApiCalls++;
            return raw;
        }

        public DexScreenerClient(ConcurrentDictionary<string, DateTime> sharedSeenSet, int workerIndex = 0, AppConfig? config = null)
        {
            _surfacedThisSession = sharedSeenSet;
            _sourceIndex = workerIndex % _dataSources.Length;
            _searchIndex = workerIndex % _searchQueries.Length;
            _config = config;
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", "DexScannerBot/4.0 (Win64)");
            _http.Timeout = TimeSpan.FromSeconds(20);
        }

        public DexScreenerClient(AppConfig? config = null)
            : this(new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase), 0, config)
        {
        }

        public async Task<(DexPair? Pair, BoostEntry? Boost)> GetNextCandidateAsync(CancellationToken ct = default)
        {
            if (_queue.Count == 0 && _pairQueue.Count == 0)
            {
                for (int attempt = 0; attempt < _dataSources.Length + 1; attempt++)
                {
                    await RefillAsync(ct);
                    if (_queue.Count > 0 || _pairQueue.Count > 0) break;
                    await Task.Delay(500, ct);
                }
            }

            if (_pairQueue.Count > 0)
            {
                var cached = _pairQueue.Dequeue();
                return cached;
            }

            if (_queue.Count == 0) return (null, null);

            while (_queue.Count > 0)
            {
                var batch = new List<BoostEntry>();
                var batchSize = _config?.Scanner?.BatchSize ?? 30;
                while (_queue.Count > 0 && batch.Count < batchSize)
                {
                    var b = _queue.Dequeue();
                    if (string.IsNullOrWhiteSpace(b.TokenAddress)) continue;

                    // Skip tokens seen within the last DEDUP_EXPIRY_SECONDS
                    if (_surfacedThisSession.TryGetValue(b.TokenAddress!, out var seenAt)
                        && (DateTime.UtcNow - seenAt).TotalSeconds < DEDUP_EXPIRY_SECONDS)
                        continue;

                    _surfacedThisSession[b.TokenAddress!] = DateTime.UtcNow;
                    batch.Add(b);
                }
                if (batch.Count == 0) continue;

                TotalFetched += batch.Count;
                var addresses = string.Join(",", batch.Select(b => b.TokenAddress));

                try
                {
                    await _apiLock.WaitAsync(ct);
                    string raw;
                    try
                    {
                        if ((DateTime.UtcNow - _lastTokensFetch).TotalSeconds < 1)
                        {
                            await Task.Delay(1000, ct);
                        }
                        raw = await TimedGetAsync(TOKENS_URL + addresses, ct);
                        _lastTokensFetch = DateTime.UtcNow;
                    }
                    finally
                    {
                        _apiLock.Release();
                    }
                    
                    var pairs = DexJson.ParsePairs(raw);

                    if (pairs.Count == 0)
                    {
                        LastError = "API returned no pairs for batch";
                        continue;
                    }

                    // Filter out zero liquidity / dead pairs right at the source
                    var bestPairPerToken = pairs
                        .Where(p => p.BaseToken != null
                                 && (p.Volume?.H24 ?? 0) >= 1000
                                 && ((p.Liquidity?.Usd ?? 0) >= 1000 || (p.DexId == "pumpfun" && (p.MarketCap > 5000 || p.Fdv > 5000))))
                        .GroupBy(p => p.BaseToken!.Address?.ToLowerInvariant() ?? "")
                        .Select(g => g.OrderByDescending(p => p.Liquidity?.Usd ?? 0).First())
                        .ToList();

                    foreach (var p in bestPairPerToken)
                    {
                        var boost = batch.FirstOrDefault(x =>
                            string.Equals(x.TokenAddress, p.BaseToken?.Address, StringComparison.OrdinalIgnoreCase));
                        if (boost != null)
                            _pairQueue.Enqueue((p, boost));
                    }

                    if (_pairQueue.Count > 0)
                    {
                        var first = _pairQueue.Dequeue();
                        return first;
                    }
                }
                catch (HttpRequestException ex) { LastError = $"HTTP error: {ex.Message}"; }
                catch (OperationCanceledException) { LastError = "API request timed out"; }
                catch (Exception ex) { LastError = $"Error: {ex.Message}"; }
            }

            return (null, null);
        }

        /// <summary>Boost/profile feeds return an array; tolerate a single object too.</summary>
        private List<BoostEntry> ParseBoostList(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                    return root.Deserialize<List<BoostEntry>>(_json) ?? new List<BoostEntry>();
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var one = root.Deserialize<BoostEntry>(_json);
                    return one != null ? new List<BoostEntry> { one } : new List<BoostEntry>();
                }
            }
            catch (Exception ex) { LastError = $"{LastSource} parse error: {ex.Message}"; }
            return new List<BoostEntry>();
        }

        public void RecordSurfaced(string? address)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            _surfacedThisSession[address] = DateTime.UtcNow;
        }

        private async Task RefillAsync(CancellationToken ct = default)
        {
            _fetchCounter++;
            // 75% Boosts & Profiles, 25% Targeted Keyword Searches
            bool doSearch = (_fetchCounter % 4 == 0);

            if (doSearch)
            {
                string query = _searchQueries[_searchIndex];
                _searchIndex = (_searchIndex + 1) % _searchQueries.Length;
                string url = $"https://api.dexscreener.com/latest/dex/search?q={query}";
                LastSource = $"Search: {query}";

                await _apiLock.WaitAsync(ct);
                try
                {
                    if ((DateTime.UtcNow - _lastListFetch).TotalSeconds < 2)
                    {
                        await Task.Delay(2000, ct);
                    }

                    var raw = await TimedGetAsync(url, ct);
                    _lastListFetch = DateTime.UtcNow;

                    var searchPairs = DexJson.ParsePairs(raw);
                    if (searchPairs.Count > 0)
                    {
                        foreach (var pair in searchPairs)
                        {
                            if (pair.BaseToken == null || string.IsNullOrWhiteSpace(pair.BaseToken.Address)) continue;
                            if ((pair.Volume?.H24 ?? 0) < 1000) continue;

                            if (_surfacedThisSession.TryGetValue(pair.BaseToken.Address, out var seenAt)
                                && (DateTime.UtcNow - seenAt).TotalSeconds < DEDUP_EXPIRY_SECONDS)
                                continue;

                            _surfacedThisSession[pair.BaseToken.Address] = DateTime.UtcNow;

                            var mockBoost = new BoostEntry
                            {
                                TokenAddress = pair.BaseToken.Address,
                                ChainId = pair.ChainId,
                                Url = pair.Url
                            };
                            _pairQueue.Enqueue((pair, mockBoost));
                        }
                    }
                    LastError = "";
                }
                catch (HttpRequestException ex) { LastError = $"{LastSource} HTTP error: {ex.Message}"; }
                catch (OperationCanceledException) { LastError = $"{LastSource} timed out"; }
                catch (Exception ex) { LastError = $"{LastSource} error: {ex.Message}"; }
                finally
                {
                    _apiLock.Release();
                }
            }
            else
            {
                string url = _dataSources[_sourceIndex];
                _sourceIndex = (_sourceIndex + 1) % _dataSources.Length;
                LastSource = url.Contains("profiles") ? "Token Profiles" :
                             url.Contains("top") ? "Top Boosted" : "Latest Boosted";

                await _apiLock.WaitAsync(ct);
                try
                {
                    if ((DateTime.UtcNow - _lastListFetch).TotalSeconds < 2)
                    {
                        await Task.Delay(2000, ct);
                    }

                    var raw = await TimedGetAsync(url, ct);
                    _lastListFetch = DateTime.UtcNow;

                    var list = ParseBoostList(raw);

                    if (list == null || list.Count == 0)
                    {
                        LastError = $"{LastSource} returned empty list";
                        return;
                    }

                    foreach (var b in list.OrderBy(_ => Guid.NewGuid()))
                        _queue.Enqueue(b);

                    LastError = "";
                }
                catch (HttpRequestException ex) { LastError = $"{LastSource} HTTP error: {ex.Message}"; }
                catch (OperationCanceledException) { LastError = $"{LastSource} timed out"; }
                catch (Exception ex) { LastError = $"{LastSource} error: {ex.Message}"; }
                finally
                {
                    _apiLock.Release();
                }
            }
        }
    }
}
