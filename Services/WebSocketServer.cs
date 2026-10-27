using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DexScannerBot.Models;

namespace DexScannerBot.Services
{
    public class WebSocketServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly ConcurrentDictionary<Guid, WebSocketContext> _clients = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _serverTask;
        private bool _disposed;
        private HistoryApiService? _historyApi;
        public int ClientCount => _clients.Count;
        public bool IsListening => _listener.IsListening;
        public event Action<string, object>? OnMessageReceived;
        public event Action<string>? OnClientConnected;

        public void SetHistoryApi(HistoryApiService api) => _historyApi = api;

        private class WebSocketContext
        {
            public Guid Id { get; set; } = Guid.NewGuid();
            public HttpListenerContext HttpContext { get; set; } = null!;
            public System.Net.WebSockets.WebSocket WebSocket { get; set; } = null!;
            public string RemoteEndpoint { get; set; } = "";
            public Task ReceiveTask { get; set; } = Task.CompletedTask;
            public bool IsAlive { get; set; } = true;
            public SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);
        }

        public WebSocketServer(string host, int port)
        {
            _listener = new HttpListener();

            // Fix: Parse and normalize the host parameter properly
            string normalizedHost = string.IsNullOrWhiteSpace(host) || host == "0.0.0.0" || host == "0.0.0.0/" || host.StartsWith("0.0.0.0/", StringComparison.OrdinalIgnoreCase)
                ? "+"
                : host.Trim('/')
                  .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                  .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                  .Replace("//", "", StringComparison.OrdinalIgnoreCase) // Remove double slashes
                  .Trim();

            // Fix: Ensure prefix is valid and includes port
            string prefix = $"http://{normalizedHost}:{port}/";
            if (!prefix.EndsWith("/")) prefix += "/";

            // Fix: Remove duplicate prefixes and validate before adding
            if (!_listener.Prefixes.Contains(prefix))
            {
                _listener.Prefixes.Add(prefix);
            }

            Console.WriteLine($"[WS] Registered prefix: {prefix}");
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            try
            {
                _listener.Start();
                Console.WriteLine("[WS] ✓ HttpListener started successfully");
            }
            catch (HttpListenerException ex)
            {
                Console.WriteLine($"[WS] ✗ HttpListener failed to start (Win32 error {ex.ErrorCode}): {ex.Message}");
                if (ex.ErrorCode == 50)
                {
                    Console.WriteLine("[WS] CAUSE: Invalid or unsupported URL prefix format.");
                    Console.WriteLine($"[WS] Registered prefix: {_listener.Prefixes.FirstOrDefault()}");
                    Console.WriteLine("[WS] FIX: Use 'localhost' or '+' as the host (not 0.0.0.0).");
                }
                else if (ex.ErrorCode == 5)
                {
                    Console.WriteLine("[WS] CAUSE: Access denied — URL ACL not reserved for this port.");
                    Console.WriteLine("[WS] FIX: Run as Administrator, or execute:");
                    string port = _listener.Prefixes.First().Split(':')[2].TrimEnd('/').TrimEnd('/');
                    Console.WriteLine($"       netsh http add urlacl url=http://+:{port}/ user=Everyone");
                }
                else
                {
                    Console.WriteLine("[WS] CAUSE: The request is not supported by the underlying HTTP.sys driver.");
                    Console.WriteLine("[WS] FIX: Check for port conflicts (netstat -ano | findstr :<port>) " +
                                      "and ensure no other process is bound to the same port.");
                }

                Console.WriteLine("[WS] ⚠ Continuing without the web dashboard. Scanning will operate in console-only mode.");
                return;
            }

            _serverTask = Task.Run(() => AcceptLoopAsync(_cts.Token), ct);
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_serverTask != null) await _serverTask;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        var wsCtx = new WebSocketContext { HttpContext = ctx };
                        // NOTE: Do NOT add to _clients here — the WebSocket isn't ready yet.
                        // Adding it here causes BroadcastAsync to hit a null WebSocket,
                        // silently catch the NullRef, and remove the client before it ever receives data.
                        _ = HandleClientAsync(wsCtx, ct);
                    }
                    else if (ctx.Request.Url?.AbsolutePath == "/")
                    {
                        await ServeDashboardAsync(ctx, ct);
                    }
                    else if (ctx.Request.Url?.AbsolutePath == "/health")
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("OK"), ct);
                        ctx.Response.Close();
                    }
                    else if (ctx.Request.Url?.AbsolutePath.StartsWith("/api/") == true)
                    {
                        await HandleApiRequestAsync(ctx, ct);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WS] Accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleApiRequestAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var url = ctx.Request.Url;
            if (url == null)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            var path = url.AbsolutePath;
            var query = url.Query;

            try
            {
                string response = "";

                // GET /api/v1/tokens/search?q={query}&chain={chain}&minScore={score}&limit={n}
                if (path == "/api/v1/tokens/search")
                {
                    var q = GetQueryParam(query, "q");
                    var chain = GetQueryParam(query, "chain");
                    var minScore = ParseDouble(GetQueryParam(query, "minScore"));
                    var maxScore = ParseDouble(GetQueryParam(query, "maxScore"));
                    var limit = ParseInt(GetQueryParam(query, "limit"), 50);
                    var results = await _historyApi!.SearchTokensAsync(q, chain, minScore, maxScore, null, limit, 0, ct);
                    response = System.Text.Json.JsonSerializer.Serialize(new { type = "token_search", payload = results });
                }
                // GET /api/v1/tokens/top?period={24h,7d,30d}&limit={n}
                else if (path == "/api/v1/tokens/top")
                {
                    var period = GetQueryParam(query, "period", "24h");
                    var limit = ParseInt(GetQueryParam(query, "limit"), 50);
                    var results = await _historyApi!.GetTopTokensAsync(period, limit, ct);
                    response = System.Text.Json.JsonSerializer.Serialize(new { type = "top_tokens", payload = results });
                }
                // GET /api/v1/tokens/{address}/history
                else if (path.StartsWith("/api/v1/tokens/") && path.EndsWith("/history"))
                {
                    var address = path.Split('/')[3];
                    var chain = GetQueryParam(query, "chain", "solana");
                    var result = await _historyApi!.GetTokenHistoryAsync(address, chain, ct);
                    response = System.Text.Json.JsonSerializer.Serialize(new { type = "token_history", payload = result });
                }
                // GET /api/v1/trades?status={status}&from={date}&to={date}
                else if (path == "/api/v1/trades")
                {
                    var status = GetQueryParam(query, "status");
                    var from = ParseDate(GetQueryParam(query, "from"));
                    var to = ParseDate(GetQueryParam(query, "to"));
                    var limit = ParseInt(GetQueryParam(query, "limit"), 100);
                    var results = await _historyApi!.GetTradeHistoryAsync(status, from, to, limit, ct);
                    response = System.Text.Json.JsonSerializer.Serialize(new { type = "trade_history", payload = results });
                }
                // GET /api/v1/analytics/summary?period={24h,7d,30d}
                else if (path == "/api/v1/analytics/summary")
                {
                    var period = GetQueryParam(query, "period", "24h");
                    var result = await _historyApi!.GetAnalyticsSummaryAsync(period, ct);
                    response = System.Text.Json.JsonSerializer.Serialize(new { type = "analytics_summary", payload = result });
                }
                // GET /api/v1/analytics/performance?period={24h,7d,30d}
                else if (path == "/api/v1/analytics/performance")
                {
                    var period = GetQueryParam(query, "period", "7d");
                    var results = await _historyApi!.GetPerformanceOverTimeAsync(period, ct);
                    response = System.Text.Json.JsonSerializer.Serialize(new { type = "performance_over_time", payload = results });
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(response);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.Headers.Add("Cache-Control", "no-cache");
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error: {ex.Message}");
                ctx.Response.StatusCode = 500;
                var errorBytes = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                ctx.Response.ContentLength64 = errorBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(errorBytes, ct);
                ctx.Response.Close();
            }
        }

        private static string GetQueryParam(string query, string key, string defaultValue = "")
        {
            if (string.IsNullOrEmpty(query)) return defaultValue;
            var pairs = query.TrimStart('?').Split('&');
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=');
                if (parts.Length >= 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(parts[1]);
            }
            return defaultValue;
        }

        private static double? ParseDouble(string value)
        {
            return double.TryParse(value, out var result) ? result : null;
        }

        private static int ParseInt(string value, int defaultValue)
        {
            return int.TryParse(value, out var result) ? result : defaultValue;
        }

        private static DateTime? ParseDate(string value)
        {
            return DateTime.TryParse(value, out var result) ? result : null;
        }

        private async Task ServeDashboardAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var baseDir = AppContext.BaseDirectory;
            
            // Try multiple locations for the frontend (dev vs published)
            var possiblePaths = new[]
            {
                // Published: frontend folder next to executable
                Path.Combine(baseDir, "frontend", "index.html"),
                // Development: relative from bin/Debug/net8.0 or bin/Release/net8.0
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "frontend", "index.html")),
                // Alternative dev path
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "frontend", "index.html")),
            };

            string? html = null;
            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    html = await File.ReadAllTextAsync(path, ct);
                    break;
                }
            }

            if (html == null)
            {
                // Fallback: embedded minimal dashboard
                html = GetEmbeddedDashboard();
            }

            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            
            // Prevent browser caching
            ctx.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            ctx.Response.Headers.Add("Pragma", "no-cache");
            ctx.Response.Headers.Add("Expires", "0");

            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }

        private static string GetEmbeddedDashboard()
        {
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>DexScannerBot v4.0 - Live Terminal</title>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  body { font-family:'Segoe UI',system-ui,sans-serif; background:#06060b; color:#e8e8f0; height:100vh; display:flex; flex-direction:column; }
  .header { padding:12px 20px; background:#0a0a18; border-bottom:1px solid #1e1e35; display:flex; justify-content:space-between; align-items:center; }
  .logo { font-weight:700; letter-spacing:2px; color:#00ff88; }
  .status { display:flex; gap:8px; align-items:center; font-size:11px; }
  .dot { width:8px; height:8px; border-radius:50%; background:#ff3366; }
  .dot.on { background:#00ff88; }
  .main { flex:1; display:grid; grid-template-columns:280px 1fr 360px; gap:1px; background:#1e1e35; overflow:hidden; }
  .panel { background:#06060b; overflow-y:auto; padding:10px; }
  .panel h3 { font-size:10px; letter-spacing:2px; color:#00ff88; margin-bottom:8px; text-transform:uppercase; }
  .feed-item { background:#0c0c14; border:1px solid #1e1e35; border-radius:8px; padding:10px; margin-bottom:6px; }
  .score { font-weight:800; font-size:18px; color:#00ff88; }
  .meta { font-size:10px; color:#7a7a9a; margin-top:4px; }
  .sysstats { font-size:12px; }
  .sysstats div { display:flex; justify-content:space-between; padding:3px 0; border-bottom:1px solid #1e1e35; }
  .footer { padding:6px 20px; background:#0c0c14; border-top:1px solid #1e1e35; font-size:10px; color:#7a7a9a; display:flex; justify-content:space-between; }
</style>
</head>
<body>
  <div class=""header"">
    <div class=""logo"">DEXSCANNER v4.0 LIVE</div>
    <div class=""status""><div class=""dot"" id=""wsDot""></div><span id=""wsLabel"">OFFLINE</span></div>
  </div>
  <div class=""main"">
    <div class=""panel""><h3>Scan Pipeline</h3><div id=""workerList""></div></div>
    <div class=""panel""><h3>Live Feed</h3><div id=""feedBody""></div></div>
    <div class=""panel""><h3>System</h3><div class=""sysstats"" id=""sysPanel""></div></div>
  </div>
  <div class=""footer""><span id=""footSrc"">Source: -</span><span id=""footTime"">-</span></div>
<script>
const state = { tokens:[], workers:[], connected:false, startTime:Date.now() };
function setStatus(on){ document.getElementById('wsDot').className='dot'+(on?' on':''); document.getElementById('wsLabel').textContent=on?'ONLINE':'OFFLINE'; }
function connect(){
  const proto = location.protocol==='https:'?'wss:':'ws:';
  const ws = new WebSocket(proto+'//'+location.host+'/ws');
  ws.onopen = () => { state.connected=true; setStatus(true); };
  ws.onclose = () => { state.connected=false; setStatus(false); setTimeout(connect,2000); };
  ws.onmessage = (e) => {
    try {
      const m = JSON.parse(e.data);
      if (m.type==='system_status') renderSystem(m.payload);
      else if (m.type==='token_surfaced') addToken(m.payload);
    } catch(err) { console.error('[WS] Parse error', err); }
  };
}
function renderSystem(s){
  const el = document.getElementById('sysPanel');
  el.innerHTML = ['WORKERS','QUEUE','API LAT','TRADES','PNL'].map(k => {
    const map = {WORKERS:s.workersActive, QUEUE:s.queueDepth, 'API LAT':(s.apiLatencyMs>0?s.apiLatencyMs+'ms':'OFFLINE'), TRADES:s.tradesToday, PNL:'$'+(s.pnlToday||0).toFixed(2)};
    return '<div><span>'+k+'</span><span>'+map[k]+'</span></div>';
  }).join('');
  if (s.workers) { state.workers=s.workers; renderWorkers(); }
  const t = Math.floor((Date.now()-state.startTime)/1000);
  document.getElementById('footTime').textContent = t<60? t+'s' : Math.floor(t/60)+'m '+(t%60)+'s';
}
function renderWorkers(){
  const el = document.getElementById('workerList');
  el.innerHTML = state.workers.map(w => '<div class=""feed-item""><div class=""score"">'+(w.workerId||'?')+'</div><div class=""meta"">'+(w.sourceFeed||'-')+'</div></div>').join('');
}
function addToken(p){
  const el = document.getElementById('feedBody');
  const item = document.createElement('div');
  item.className = 'feed-item';
  item.innerHTML = '<div class=""score"">'+Math.round(p.totalScore||0)+'</div><div class=""meta"">'+(p.symbol||'')+' | '+(p.chain||'')+' · '+(p.dex||'')+'</div>';
  el.prepend(item);
  while (el.children.length > 50) el.removeChild(el.lastChild);
}
connect();
</script>
</body>
</html>";
        }

        private async Task HandleClientAsync(WebSocketContext wsCtx, CancellationToken ct)
        {
            try
            {
                var wsContext = await wsCtx.HttpContext.AcceptWebSocketAsync(null);
                var ws = wsContext.WebSocket;
                wsCtx.RemoteEndpoint = wsCtx.HttpContext.Request.RemoteEndPoint?.ToString() ?? "unknown";
                wsCtx.WebSocket = ws;

                // Add to clients AFTER the WebSocket handshake is complete and WebSocket is set.
                // This prevents BroadcastAsync from seeing a null WebSocket.
                _clients.TryAdd(wsCtx.Id, wsCtx);
                Console.WriteLine($"[WS] Client {wsCtx.RemoteEndpoint} connected (id: {wsCtx.Id})");
                try
                {
                    OnClientConnected?.Invoke(wsCtx.Id.ToString());
                }
                catch { }

                var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = Task.Run(async () =>
                {
                    while (!sendCts.Token.IsCancellationRequested && ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        try { await Task.Delay(1000, sendCts.Token); }
                        catch { break; }
                    }
                }, sendCts.Token);

                var buffer = new byte[65536];
                while (ws.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    try
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                        {
                            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);

                            try
                            {
                                OnMessageReceived?.Invoke(wsCtx.RemoteEndpoint, msg);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[WS] Handler error for {wsCtx.RemoteEndpoint}: {ex.Message}");
                            }

                            try
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(msg);
                                if (doc.RootElement.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "heartbeat")
                                {
                                    var ack = Encoding.UTF8.GetBytes("{\"type\":\"heartbeat\"}");
                                    await wsCtx.SendLock.WaitAsync(ct);
                                    try
                                    {
                                        await ws.SendAsync(new ArraySegment<byte>(ack), System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
                                    }
                                    finally
                                    {
                                        wsCtx.SendLock.Release();
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        // Check if this is an expected disconnect (WebSocket in closed/aborted state)
                        if (ws.State == System.Net.WebSockets.WebSocketState.Aborted ||
                            ws.State == System.Net.WebSockets.WebSocketState.Closed ||
                            ws.State == System.Net.WebSockets.WebSocketState.CloseSent ||
                            ws.State == System.Net.WebSockets.WebSocketState.CloseReceived)
                        {
                            // Expected disconnect - log at debug level
                            Console.WriteLine($"[WS] Client {wsCtx.RemoteEndpoint} disconnected (state: {ws.State})");
                        }
                        else
                        {
                            // Unexpected error
                            Console.WriteLine($"[WS] Receive error for {wsCtx.RemoteEndpoint}: {ex.Message}");
                        }
                        break;
                    }
                }

                wsCtx.IsAlive = false;
                _clients.TryRemove(wsCtx.Id, out _);
                try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", ct); } catch { }
            }
            catch { }
        }

        public async Task BroadcastAsync(string jsonMessage, CancellationToken ct = default)
        {
            var buffer = Encoding.UTF8.GetBytes(jsonMessage);
            var clientsList = _clients.ToList();
            if (clientsList.Count == 0) return;

            var tasks = clientsList.Select(async kvp =>
            {
                if (!kvp.Value.IsAlive)
                {
                    _clients.TryRemove(kvp.Key, out _);
                    return;
                }
                try
                {
                    var clientWs = kvp.Value.WebSocket;
                    if (clientWs != null && clientWs.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        await kvp.Value.SendLock.WaitAsync(ct);
                        try
                        {
                            await clientWs.SendAsync(
                                new ArraySegment<byte>(buffer),
                                System.Net.WebSockets.WebSocketMessageType.Text,
                                true, ct);
                        }
                        finally
                        {
                            kvp.Value.SendLock.Release();
                        }
                    }
                }
                catch
                {
                    _clients.TryRemove(kvp.Key, out _);
                }
            });

            await Task.WhenAll(tasks);
        }

        public async Task SendToClientAsync(string clientId, string jsonMessage, CancellationToken ct = default)
        {
            if (!Guid.TryParse(clientId, out var id)) return;
            if (_clients.TryGetValue(id, out var wsCtx) && wsCtx.IsAlive)
            {
                try
                {
                    var buffer = Encoding.UTF8.GetBytes(jsonMessage);
                    await wsCtx.SendLock.WaitAsync(ct);
                    try
                    {
                        await wsCtx.WebSocket.SendAsync(
                            new ArraySegment<byte>(buffer),
                            System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
                    }
                    finally
                    {
                        wsCtx.SendLock.Release();
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
            foreach (var c in _clients.Values)
            {
                    try { c.WebSocket.Dispose(); } catch { }
            }
        }
    }
}
