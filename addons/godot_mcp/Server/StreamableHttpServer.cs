using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Godot;

namespace GodotMcpPro.Server;

/// <summary>
/// MCP Streamable HTTP transport hosted inside the Godot editor.
///
 /// Endpoint: POST http://127.0.0.1:{port}/mcp  (default port 68001, scans up to 68005)
///
/// Threading model: HTTP accept/read runs on background threads, but ALL MCP
/// handling (JSON parsing, tool execution, Godot API access) is marshaled to
/// the main thread. <see cref="ProcessPending"/> must be pumped from _Process.
/// The actual bound port is written to user://mcp_http_port for the stdio bridge.
/// </summary>
public sealed class StreamableHttpServer : IDisposable
{
    public const int DefaultBasePort = 68001;
    public const int PortScanRange = 5;
    private const string PortFilePath = "user://mcp_http_port";

    private sealed record PendingRequest(string Body, TaskCompletionSource<string?> Completion);

    private readonly McpProtocolHandler _handler;
    private readonly ConcurrentQueue<PendingRequest> _pending = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int Port { get; private set; } = -1;
    public bool IsRunning => _listener?.IsListening ?? false;

    /// <summary>Raised on the main thread for every handled request: (method summary, ok).</summary>
    public event Action<string>? RequestLogged;

    public StreamableHttpServer(McpProtocolHandler handler) => _handler = handler;

    /// <summary>Bind and start listening. Scans ports base..base+range-1.</summary>
    public Godot.Error Start(int? overridePort = null)
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
        while (!ct.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                break; // listener stopped
            }
            _ = Task.Run(() => HandleHttpContext(ctx), ct);
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
