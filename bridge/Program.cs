using System.Text;

namespace GodotMcpPro.Bridge;

/// <summary>
/// stdio &lt;-&gt; Streamable HTTP bridge for the in-editor Godot MCP server.
///
/// MCP clients that only speak stdio launch this executable; every JSON-RPC
/// message read from stdin is POSTed to the editor's /mcp endpoint and the
/// response is written back to stdout (newline-delimited JSON per the MCP
/// stdio transport spec).
///
/// Usage:
 ///   godot-mcp-bridge [--port 65001] [--url http://127.0.0.1:65001/mcp]
 /// Port resolution order: --url > --port > GODOT_MCP_HTTP_PORT env > scan 65001-65005.
/// </summary>
internal static class Program
{
    private const int DefaultBasePort = 65001;
    private const int PortScanRange = 5;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static async Task<int> Main(string[] args)
    {
        var url = ResolveExplicitUrl(args);

        // stdout must carry ONLY protocol messages; all diagnostics go to stderr.
        var stdout = Console.OpenStandardOutput();
        using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            url ??= await DiscoverEndpoint();
            if (url == null)
            {
                await writer.WriteLineAsync(ErrorResponse(line,
                    "Godot editor MCP server not reachable. Open your project in Godot with the "
                    + "Godot MCP Pro CSharp plugin enabled, or pass --url/--port explicitly."));
                continue;
            }

            string? response;
            try
            {
                response = await Forward(url, line);
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync($"[bridge] forward failed: {e.Message}");
                url = null; // editor may have restarted on a different port — rediscover
                response = ErrorResponse(line, $"Failed to reach Godot editor: {e.Message}");
            }

            if (response != null)
                await writer.WriteLineAsync(response);
        }

        return 0;
    }

    /// <summary>POST one JSON-RPC message. Returns null for notifications (HTTP 202).</summary>
    private static async Task<string?> Forward(string url, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        using var response = await Http.SendAsync(request);

        if ((int)response.StatusCode == 202)
            return null;

        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Truncate(text, 200)}");
        return text.ReplaceLineEndings(" ").Trim();
    }

    private static string? ResolveExplicitUrl(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--url":
                    return args[i + 1];
                case "--port" when int.TryParse(args[i + 1], out var p):
                    return $"http://127.0.0.1:{p}/mcp";
            }
        }
        var env = Environment.GetEnvironmentVariable("GODOT_MCP_HTTP_PORT");
        if (int.TryParse(env, out var envPort))
            return $"http://127.0.0.1:{envPort}/mcp";
        return null;
    }

    /// <summary>Probe the scan range for a live editor endpoint (GET /mcp answers 405 with an MCP hint).</summary>
    private static async Task<string?> DiscoverEndpoint()
    {
        for (var port = DefaultBasePort; port < DefaultBasePort + PortScanRange; port++)
        {
            var url = $"http://127.0.0.1:{port}/mcp";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                using var response = await Http.GetAsync(url, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                if ((int)response.StatusCode == 405 && body.Contains("MCP", StringComparison.OrdinalIgnoreCase))
                {
                    await Console.Error.WriteLineAsync($"[bridge] connected to Godot editor at {url}");
                    return url;
                }
            }
            catch (Exception)
            {
                // port not listening — keep scanning
            }
        }
        return null;
    }

    /// <summary>Build a JSON-RPC error response echoing the request id (null-safe, no JSON dependency on request shape).</summary>
    private static string ErrorResponse(string requestJson, string message)
    {
        var id = ExtractId(requestJson) ?? "null";
        var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32001,\"message\":\"{escaped}\"}}}}";
    }

    /// <summary>Extract the raw "id" token from a JSON-RPC message with minimal parsing.</summary>
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
