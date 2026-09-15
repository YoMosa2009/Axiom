using Malx_AI;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Exercises the built production HTTP/SSE code against loopback providers. No model, credentials,
// desktop input, or external network is used.
Environment.SetEnvironmentVariable("AXIOM_DATA_DIR", Path.Combine(Path.GetTempPath(), "axiom-transport-probe-" + Guid.NewGuid()));

await RunStreamInterruptionProbeAsync();
await RunEndpointDiscoveryProbeAsync();
return;

// ---------------------------------------------------------------------------------------------
// 1. A provider that dies mid-stream must surface immediately, must not yield partial action
//    JSON, and must not silently retry with the stale request.
// ---------------------------------------------------------------------------------------------
static async Task RunStreamInterruptionProbeAsync()
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    int chatRequests = 0;
    Task server = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            using var stream = client.GetStream();
            (string requestLine, _) = await ReadRequestAsync(stream, stop.Token);
            bool chat = requestLine.Contains("/chat/completions");
            string content;
            if (chat)
            {
                int number = Interlocked.Increment(ref chatRequests);
                const string error = "data: {\"error\":{\"message\":\"model runner has unexpectedly stopped\"}}\n\n";
                string partial = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = "{\"action\":{\"type\":\"click\",\"x\":10,\"y\":20,\"expected_state\":\"menu opens\"}}" } } } }) + "\n\n";
                content = number switch
                {
                    1 => error,
                    2 => partial + error,
                    _ => "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n"
                };
            }
            else content = "{\"data\":[{\"id\":\"test-vision\"}],\"capabilities\":[\"vision\"],\"model_info\":{\"context_length\":8192}}";

            await WriteResponseAsync(stream, 200, content, chat ? "text/event-stream" : "application/json", stop.Token);
        }
    }, stop.Token);

    try
    {
        var service = new OpenRouterChatService();
        service.SetCustomEndpoint($"http://127.0.0.1:{port}/v1", "probe-key", "test-vision", 8192, true);
        async Task<OpenRouterChatResponse> Infer() => await service.SendConversationStreamAsync(
            [new("user", "Return one short JSON action.")], "Return JSON only.", modelId: OpenRouterChatService.CustomEndpointModelId,
            maxTokensOverride: 128, allowModelFallback: false, requireCompleteResponse: true, cancellationToken: stop.Token);
        for (int expected = 1; expected <= 2; expected++)
        {
            try { await Infer(); throw new Exception("Interrupted output was incorrectly accepted."); }
            catch (HttpRequestException ex) when (ex.Message.Contains("runner has unexpectedly stopped")) { }
            if (chatRequests != expected) throw new Exception("Hidden inference retry reused the stale request.");
        }
        if ((await Infer()).Text != "ok" || chatRequests != 3) throw new Exception("Fresh inference did not recover.");
        Console.WriteLine("PASS: provider crash surfaced immediately; partial action JSON rejected; no hidden retries; subsequent fresh request succeeded.");
    }
    finally
    {
        stop.Cancel(); listener.Stop();
        try { await server; } catch (OperationCanceledException) { } catch (SocketException) { }
    }
}

// ---------------------------------------------------------------------------------------------
// 2. Context-window discovery must reach the server's OWN api below /v1, must prefer the served
//    window over the model's trained capacity, and must admit when it found nothing rather than
//    presenting the fallback as discovered. Each case answers only the paths its real server
//    answers and 404s everything else, so probe ORDER and URL construction are both under test —
//    neither is covered by the parser unit tests.
// ---------------------------------------------------------------------------------------------
static async Task RunEndpointDiscoveryProbeAsync()
{
    (string Name, Dictionary<string, string> Routes, int Expected, bool Advertised)[] servers =
    [
        ("Ollama", new()
        {
            ["/api/ps"] = """{"models":[{"name":"gemma3:12b","model":"gemma3:12b","context_length":16384}]}""",
            ["/v1/models"] = """{"object":"list","data":[{"id":"gemma3:12b","object":"model","owned_by":"library"}]}""",
            ["/api/show"] = """{"model_info":{"gemma3.context_length":131072},"capabilities":["completion","vision"]}"""
        }, 16384, true),

        ("LM Studio", new()
        {
            ["/api/v0/models"] = """{"data":[{"id":"gemma3-12b","type":"vlm","state":"loaded","max_context_length":131072,"loaded_context_length":24576}]}""",
            ["/v1/models"] = """{"object":"list","data":[{"id":"gemma3-12b","object":"model"}]}"""
        }, 24576, true),

        ("llama.cpp", new()
        {
            ["/props"] = """{"default_generation_settings":{"n_ctx":12288},"total_slots":1,"model_info":{"n_ctx_train":131072}}""",
            ["/v1/models"] = """{"object":"list","data":[{"id":"gemma3-12b","object":"model"}]}"""
        }, 12288, true),

        ("KoboldCpp", new()
        {
            ["/api/v1/config/max_context_length"] = """{"value":40960}""",
            ["/v1/models"] = """{"object":"list","data":[{"id":"gemma3-12b","object":"model"}]}"""
        }, 40960, true),

        ("vLLM", new()
        {
            ["/v1/models"] = """{"object":"list","data":[{"id":"gemma3-12b","object":"model","owned_by":"vllm","max_model_len":65536}]}"""
        }, 65536, true),

        // Nothing anywhere reports a window. The fallback must be used AND reported as a fallback.
        ("Unrecognised server", new()
        {
            ["/v1/models"] = """{"object":"list","data":[{"id":"gemma3-12b","object":"model"}]}"""
        }, OpenRouterChatService.CustomEndpointContextWindowTokens, false)
    ];

    foreach ((string name, Dictionary<string, string> routes, int expected, bool advertised) in servers)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                using var stream = client.GetStream();
                (string requestLine, _) = await ReadRequestAsync(stream, stop.Token);
                string path = requestLine.Split(' ') is [_, string target, ..] ? target : "";
                bool known = routes.TryGetValue(path, out string? body);
                await WriteResponseAsync(stream, known ? 200 : 404, known ? body! : """{"error":"not found"}""",
                    "application/json", stop.Token);
            }
        }, stop.Token);

        try
        {
            var service = new OpenRouterChatService();
            service.SetCustomEndpoint($"http://127.0.0.1:{port}/v1", "probe-key", "gemma3-12b");
            await service.RefreshCustomEndpointMetadataAsync(stop.Token, force: true);

            int resolved = service.CustomEndpointResolvedContextWindowTokens;
            bool resolvedAdvertised = service.CustomEndpointContextWindowIsAdvertised;
            if (resolved != expected)
                throw new Exception($"{name}: expected a {expected} token window, resolved {resolved}.");
            if (resolvedAdvertised != advertised)
                throw new Exception($"{name}: expected advertised={advertised}, got {resolvedAdvertised}.");

            Console.WriteLine($"PASS: {name} -> {resolved:N0} tokens ({(resolvedAdvertised ? "reported by server" : "fallback, reported as such")}).");
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { } catch (SocketException) { }
        }
    }
}

static async Task<(string RequestLine, string Body)> ReadRequestAsync(NetworkStream stream, CancellationToken token)
{
    var headerBytes = new List<byte>();
    byte[] one = new byte[1];
    while (headerBytes.Count < 65536)
    {
        await stream.ReadExactlyAsync(one, token);
        headerBytes.Add(one[0]);
        int n = headerBytes.Count;
        if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10
            && headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10) break;
    }

    string[] lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n");
    int length = 0;
    foreach (string header in lines)
        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..]);
    byte[] body = new byte[length];
    if (length > 0) await stream.ReadExactlyAsync(body, token);
    return (lines[0], Encoding.UTF8.GetString(body));
}

static async Task WriteResponseAsync(NetworkStream stream, int status, string content, string contentType, CancellationToken token)
{
    byte[] bytes = Encoding.UTF8.GetBytes(content);
    byte[] headers = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(headers, token);
    await stream.WriteAsync(bytes, token);
}
