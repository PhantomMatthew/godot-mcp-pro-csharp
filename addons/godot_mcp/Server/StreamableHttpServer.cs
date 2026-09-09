using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using Godot;

namespace GodotMcpPro.Server;

/// <summary>
/// MCP Streamable HTTP transport hosted inside the Godot editor.
///
 /// Endpoint: POST http://127.0.0.1:{port}/mcp  (default port 65001, scans up to 65005)
///
/// Threading model: HTTP accept/read runs on background threads, but ALL MCP
/// handling (JSON parsing, tool execution, Godot API access) is marshaled to
/// the main thread. <see cref="ProcessPending"/> must be pumped from _Process.
/// The actual bound port is written to user://mcp_http_port for the stdio bridge.
///
/// Self-healing: transient accept failures are retried in place; if the accept
/// loop dies while the socket stays bound (zombie listener), the watchdog in
/// <see cref="ProcessPending"/> rebuilds the listener so clients reconnect
/// without an editor restart.
/// </summary>
public sealed class StreamableHttpServer : IDisposable
{
    public const int DefaultBasePort = 65001;
    public const int PortScanRange = 5;
    private const string PortFilePath = "user://mcp_http_port";

    // Must stay below the bridge's 120 s HttpClient timeout so clients receive
    // a JSON-RPC error instead of a transport-level abort.
    private const int RequestTimeoutSeconds = 90;
    private const int AcceptRetryDelayMs = 500;
    private const int MaxConsecutiveAcceptFailures = 20;
    private const long RestartCooldownMs = 5000;

    private sealed record PendingRequest(string Body, TaskCompletionSource<string?> Completion);

    private readonly McpProtocolHandler _handler;
    private readonly ConcurrentQueue<PendingRequest> _pending = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _watchdogArmed;
    private long _nextRestartTick;

    public int Port { get; private set; } = -1;
    public bool IsRunning => _listener?.IsListening ?? false;

    /// <summary>Raised on the main thread for every handled request: (method summary, ok).</summary>
    public event Action<string>? RequestLogged;

    /// <summary>Raised on the main thread after the watchdog rebuilt the listener (the port may have changed).</summary>
    public event Action? ListenerRestarted;

    public StreamableHttpServer(McpProtocolHandler handler) => _handler = handler;

    /// <summary>Bind and start listening. Scans ports base..base+range-1.</summary>
    public Godot.Error Start(int? overridePort = null) => BindListener(overridePort);

    private Godot.Error BindListener(int? overridePort)
    {
        var basePort = overridePort
            ?? (int.TryParse(System.Environment.GetEnvironmentVariable("GODOT_MCP_HTTP_PORT"), out var envPort)
                ? envPort
                : DefaultBasePort);
        var range = overridePort.HasValue ? 1 : PortScanRange;

        for (var i = 0; i < range; i++)
        {
            var port = basePort + i;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (Exception)
            {
                listener.Close();
            }
        }

        if (_listener == null)
        {
            GD.PushError($"[MCP] Could not bind HTTP port in range {basePort}-{basePort + range - 1}");
            return Godot.Error.CantCreate;
        }

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoop(_cts.Token));
        _watchdogArmed = true;
        WritePortFile();
        GD.Print($"[MCP] Streamable HTTP listening on http://127.0.0.1:{Port}/mcp");
        return Godot.Error.Ok;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception)
        {
            // ignore shutdown races
        }
        _listener = null;
        _acceptLoop = null;
        _cts = null;
        RemovePortFile();
        // Fail any queued requests so HTTP threads don't hang.
        while (_pending.TryDequeue(out var item))
            item.Completion.TrySetResult(
                "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"Server shutting down\"}}");
    }

    public void Dispose() => Stop();

    // ── Background accept loop ─────────────────────────────────────────────────

    private async Task AcceptLoop(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var listener = _listener;
            if (listener is not { IsListening: true })
                return; // Stop() closed the listener

            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception e)
            {
                if (!listener.IsListening || ct.IsCancellationRequested)
                    return;
                // Transient failure (malformed request, socket error, fd pressure):
                // keep the port and retry. After too many consecutive failures we
                // exit so the watchdog in ProcessPending can rebuild the listener.
                if (++failures >= MaxConsecutiveAcceptFailures)
                {
                    GD.PushError($"[MCP] HTTP accept failed {failures} times — abandoning listener for watchdog restart: {e.Message}");
                    return;
                }
                GD.PushWarning($"[MCP] HTTP accept failed ({failures}/{MaxConsecutiveAcceptFailures}), retrying in {AcceptRetryDelayMs} ms: {e.Message}");
                try { await Task.Delay(AcceptRetryDelayMs, ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            failures = 0;
            // CancellationToken.None: a handler queued when Stop() races in must
            // still run and close/abort its response, or the socket leaks.
            _ = Task.Run(() => HandleHttpContext(ctx), CancellationToken.None);
        }
    }

    private async Task HandleHttpContext(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Mcp-Session-Id"] = _sessionId;

        try
        {
            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path != "/mcp")
            {
                await WriteResponse(res, 404, "application/json",
                    "{\"error\":\"Not found. MCP endpoint is /mcp\"}");
                return;
            }

            switch (req.HttpMethod)
            {
                case "POST":
                {
                    string body;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                        body = await reader.ReadToEndAsync();

                    var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending.Enqueue(new PendingRequest(body, tcs));

                    // A stalled main-thread pump or a hung tool must not pin this
                    // connection (and its fds) forever; see RequestTimeoutSeconds.
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeoutSeconds));
                    await Task.WhenAny(tcs.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));
                    if (!tcs.Task.IsCompleted)
                    {
                        GD.PushError($"[MCP] Request timed out after {RequestTimeoutSeconds}s — main thread stalled or tool hung");
                        await WriteResponse(res, 200, "application/json",
                            $"{{\"jsonrpc\":\"2.0\",\"id\":{ExtractId(body) ?? "null"},"
                            + $"\"error\":{{\"code\":-32603,\"message\":\"Request timed out after {RequestTimeoutSeconds}s (editor main thread busy or tool hung)\"}}}}");
                        return;
                    }

                    var response = await tcs.Task;
                    if (response == null)
                    {
                        // Notification: 202 Accepted, no body.
                        res.StatusCode = 202;
                        res.Close();
                    }
                    else
                    {
                        await WriteResponse(res, 200, "application/json", response);
                    }
                    break;
                }
                case "DELETE":
                    // Session termination — stateless server, always OK.
                    res.StatusCode = 200;
                    res.Close();
                    break;
                case "GET":
                    // No server-initiated SSE stream support.
                    await WriteResponse(res, 405, "application/json",
                        "{\"error\":\"Method not allowed. POST JSON-RPC messages to /mcp\"}");
                    break;
                default:
                    await WriteResponse(res, 405, "application/json", "{\"error\":\"Method not allowed\"}");
                    break;
            }
        }
        catch (Exception)
        {
            try { res.Abort(); } catch (Exception) { /* connection already gone */ }
        }
    }

    private static async Task WriteResponse(HttpListenerResponse res, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    // ── Main-thread pump ───────────────────────────────────────────────────────

    /// <summary>Call from _Process on the main thread. Dispatches queued HTTP bodies to the MCP handler.</summary>
    public void ProcessPending()
    {
        while (_pending.TryDequeue(out var item))
            _ = HandleOnMainThread(item);
        EnsureListenerHealthy();
    }

    // Zombie listener detection: the accept loop exited but the socket is still
    // bound, so clients connect and hang forever. Rebuild the listener (same
    // port first, port-scan fallback) so recovery needs no editor restart.
    private void EnsureListenerHealthy()
    {
        if (!_watchdogArmed)
            return; // never started (intentional Stop disarms the pump owner anyway)

        var zombie = _listener is { IsListening: true } && _acceptLoop is { IsCompleted: true };
        var dead = _listener is not { IsListening: true };
        if (!zombie && !dead)
            return;

        var now = DateTimeOffset.UtcNow.UtcTicks;
        if (now < _nextRestartTick)
            return; // restart failed recently — don't churn every frame
        _nextRestartTick = now + RestartCooldownMs * TimeSpan.TicksPerMillisecond;

        if (zombie)
            GD.PushError("[MCP] HTTP accept loop died while the socket is still bound — rebuilding listener");

        var port = Port;
        Stop();
        if (BindListener(port) != Godot.Error.Ok)
            BindListener(null); // exact port no longer free — fall back to the port scan
        ListenerRestarted?.Invoke();
    }

    private async Task HandleOnMainThread(PendingRequest item)
    {
        try
        {
            var response = await _handler.HandleMessage(item.Body);
            item.Completion.TrySetResult(response);
            RequestLogged?.Invoke(Summarize(item.Body));
        }
        catch (Exception e)
        {
            GD.PushError($"[MCP] Unhandled protocol error: {e}");
            item.Completion.TrySetResult(
                "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"Internal server error\"}}");
        }
    }

    private static string Summarize(string body)
    {
        var parsed = Json.ParseString(body);
        if (parsed.VariantType != Variant.Type.Dictionary)
            return "invalid-json";
        var d = parsed.AsGodotDictionary();
        var method = d.TryGetValue("method", out var m) ? m.AsString() : "?";
        if (method == "tools/call"
            && d.TryGetValue("params", out var p)
            && p.VariantType == Variant.Type.Dictionary
            && p.AsGodotDictionary().TryGetValue("name", out var n))
            return $"tools/call {n.AsString()}";
        return method;
    }

    /// <summary>Extract the raw "id" token from a JSON-RPC message (same minimal parse as the stdio bridge).</summary>
    private static string? ExtractId(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out var id))
                return id.GetRawText();
        }
        catch (Exception)
        {
            // fall through
        }
        return null;
    }

    // ── Port discovery file (used by the stdio bridge) ────────────────────────

    private void WritePortFile()
    {
        using var f = Godot.FileAccess.Open(PortFilePath, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(Port.ToString());
    }

    private static void RemovePortFile()
    {
        if (Godot.FileAccess.FileExists(PortFilePath))
            DirAccess.RemoveAbsolute(PortFilePath);
    }
}
