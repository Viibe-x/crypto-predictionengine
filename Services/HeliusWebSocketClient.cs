using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    /// <summary>
    /// Helius WebSocket client for real-time Solana new pool detection.
    /// 
    /// Subscribes to:
    /// 1. Program logs for Raydium/Orca/PumpFun pool creation
    /// 2. Account notifications for LP mint events
    /// 3. Mempool transactions for sniping launches
    /// 
    /// API Docs: https://docs.helius.dev/solana-apis/enhanced-transactions-api
    ///           https://docs.helius.dev/solana-apis/websockets-api
    /// </summary>
    public class HeliusWebSocketClient : IDisposable
    {
        private readonly string _apiKey;
        private readonly string _wsUrl;
        private readonly AppConfig _config;
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private bool _isConnected;
        private readonly SemaphoreSlim _reconnectLock = new(1, 1);
        private int _reconnectAttempts;
        private const int MAX_RECONNECT_ATTEMPTS = 10;
        private const int RECONNECT_BASE_DELAY_MS = 1000;

        // Known DEX program IDs for pool creation detection
        private static readonly HashSet<string> DexProgramIds = new(StringComparer.OrdinalIgnoreCase)
        {
            // Raydium AMM
            "675kPX9MHTjS2zt1qfr1NYHuzeLXfQM9H24wFSUt1Mp8",
            // Raydium CLMM
            "CAMMCzo5YL8w4VFF8KVHrK22GGUsp5VTaW7grrKgrWqK",
            // Orca Whirlpool
            "whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc",
            // PumpFun
            "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P",
            // Meteora
            "LBUZKhRxPF3XUpBCjp4YzTKgLccjZhTSDM9YuVaPwxo"
        };

        public event Action<HeliusPoolEvent>? OnNewPoolDetected;
        public event Action<string, string>? OnError;
        public event Action? OnConnected;
        public event Action? OnDisconnected;

        public bool IsConnected => _isConnected;
        public int ReconnectAttempts => _reconnectAttempts;

        public HeliusWebSocketClient(AppConfig config)
        {
            _config = config;
            _apiKey = Environment.GetEnvironmentVariable("HELIUS_API_KEY") ?? "";
            _wsUrl = $"wss://mainnet.helius-rpc.com/?api-key={_apiKey}";
        }

        public bool IsEnabled => !string.IsNullOrEmpty(_apiKey);

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (!IsEnabled)
            {
                Console.WriteLine("[HELIUS] ⚠ No API key configured - WebSocket disabled");
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await ConnectAsync(_cts.Token);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            _isConnected = false;

            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None);
                }
                catch { }
            }

            _ws?.Dispose();
            _ws = null;
            OnDisconnected?.Invoke();
        }

        private async Task ConnectAsync(CancellationToken ct)
        {
            await _reconnectLock.WaitAsync(ct);
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                await _ws.ConnectAsync(new Uri(_wsUrl), ct);
                _isConnected = true;
                _reconnectAttempts = 0;
                Console.WriteLine("[HELIUS] ✓ WebSocket connected");
                OnConnected?.Invoke();

                // Subscribe to program logs for pool creation
                await SubscribeToProgramLogs(ct);
                
                // Start receiving messages
                _receiveTask = Task.Run(() => ReceiveLoopAsync(ct), ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HELIUS] Connection failed: {ex.Message}");
                OnError?.Invoke("connect", ex.Message);
                _isConnected = false;
                await AttemptReconnectAsync(ct);
            }
            finally
            {
                _reconnectLock.Release();
            }
        }

        private async Task SubscribeToProgramLogs(CancellationToken ct)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;

            // Subscribe to logs for all known DEX programs
            var subscribeMsg = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "logsSubscribe",
                @params = new object[]
                {
                    new { mentions = DexProgramIds.ToArray() },
                    new { commitment = "confirmed" }
                }
            };

            var json = JsonSerializer.Serialize(subscribeMsg);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            Console.WriteLine("[HELIUS] Subscribed to DEX program logs");
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[65536];

            while (!ct.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                try
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await HandleDisconnectAsync(ct);
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    var json = sb.ToString();
                    ProcessMessage(json);
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"[HELIUS] WebSocket error: {ex.Message}");
                    OnError?.Invoke("receive", ex.Message);
                    await HandleDisconnectAsync(ct);
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HELIUS] Receive error: {ex.Message}");
                    OnError?.Invoke("receive", ex.Message);
                }
            }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Check for method field to identify notification type
                if (root.TryGetProperty("method", out var method))
                {
                    var methodStr = method.GetString();
                    
                    if (methodStr == "logsNotification")
                    {
                        ProcessLogNotification(root.GetProperty("params"));
                    }
                }
                // Check for subscription confirmation
                else if (root.TryGetProperty("result", out var result))
                {
                    Console.WriteLine($"[HELIUS] Subscription confirmed: {result}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HELIUS] Parse error: {ex.Message}");
            }
        }

        private void ProcessLogNotification(JsonElement paramsElement)
        {
            try
            {
                if (!paramsElement.TryGetProperty("result", out var result)) return;
                if (!result.TryGetProperty("value", out var value)) return;

                var signature = value.TryGetProperty("signature", out var sig) ? sig.GetString() ?? "" : "";
                
                if (!value.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array) return;

                // Check for pool creation patterns in logs
                foreach (var log in logs.EnumerateArray())
                {
                    var logStr = log.GetString() ?? "";
                    
                    // Raydium pool creation signature
                    if (logStr.Contains("initialize2", StringComparison.OrdinalIgnoreCase) ||
                        logStr.Contains("Initialize", StringComparison.OrdinalIgnoreCase))
                    {
                        var poolEvent = new HeliusPoolEvent
                        {
                            Signature = signature,
                            DetectedAt = DateTime.UtcNow,
                            Source = "helius",
                            RawLogs = new List<string>()
                        };

                        foreach (var l in logs.EnumerateArray())
                        {
                            poolEvent.RawLogs.Add(l.GetString() ?? "");
                        }

                        // Try to extract pool address from logs
                        poolEvent.PoolAddress = ExtractPoolAddress(logs);
                        poolEvent.DexProgram = ExtractDexProgram(logs);

                        Console.WriteLine($"[HELIUS] 🏊 New pool detected: {poolEvent.PoolAddress} via {poolEvent.DexProgram}");
                        OnNewPoolDetected?.Invoke(poolEvent);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HELIUS] Log processing error: {ex.Message}");
            }
        }

        private string ExtractPoolAddress(JsonElement logs)
        {
            foreach (var log in logs.EnumerateArray())
            {
                var logStr = log.GetString() ?? "";
                // Look for address patterns in logs
                if (logStr.Contains("create pool", StringComparison.OrdinalIgnoreCase) ||
                    logStr.Contains("Pool created", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the address from the log
                    var parts = logStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        if (part.Length >= 32 && part.Length <= 44 && part.All(c => char.IsLetterOrDigit(c)))
                        {
                            return part;
                        }
                    }
                }
            }
            return "";
        }

        private string ExtractDexProgram(JsonElement logs)
        {
            foreach (var log in logs.EnumerateArray())
            {
                var logStr = log.GetString() ?? "";
                if (DexProgramIds.Any(p => logStr.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    return DexProgramIds.First(p => logStr.Contains(p, StringComparison.OrdinalIgnoreCase));
                }
            }
            return "unknown";
        }

        private async Task HandleDisconnectAsync(CancellationToken ct)
        {
            if (!_isConnected) return;
            _isConnected = false;
            OnDisconnected?.Invoke();
            Console.WriteLine("[HELIUS] WebSocket disconnected");
            await AttemptReconnectAsync(ct);
        }

        private async Task AttemptReconnectAsync(CancellationToken ct)
        {
            if (_reconnectAttempts >= MAX_RECONNECT_ATTEMPTS)
            {
                Console.WriteLine("[HELIUS] Max reconnection attempts reached - giving up");
                OnError?.Invoke("reconnect", "Max reconnection attempts reached");
                return;
            }

            _reconnectAttempts++;
            var delay = RECONNECT_BASE_DELAY_MS * (int)Math.Pow(2, _reconnectAttempts - 1);
            delay = Math.Min(delay, 30000); // Cap at 30 seconds

            Console.WriteLine($"[HELIUS] Reconnecting in {delay}ms (attempt {_reconnectAttempts}/{MAX_RECONNECT_ATTEMPTS})...");
            await Task.Delay(delay, ct);

            if (!ct.IsCancellationRequested)
            {
                await ConnectAsync(ct);
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _ws?.Dispose();
            _reconnectLock.Dispose();
        }
    }

    public class HeliusPoolEvent
    {
        public string Signature { get; set; } = "";
        public string PoolAddress { get; set; } = "";
        public string DexProgram { get; set; } = "";
        public string Source { get; set; } = "helius";
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
        public List<string> RawLogs { get; set; } = new();
    }
}
