using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace IntoChat.OS.Kernel.Tests.Integration;

public sealed class NginxProxyFacts : IAsyncLifetime
{
    private static readonly byte[] LargeBody = CreateLargeBody();
    private HttpListener? _kernel;
    private CancellationTokenSource? _cts;
    private Task? _kernelLoop;
    private int _kernelPort;
    private string? _containerId;
    private string? _tempHtmlDir;
    private string? _proxyBaseUrl;
    private bool _dockerAvailable;

    private static byte[] CreateLargeBody()
    {
        var body = new byte[256 * 1024];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i % 256);
        }
        return body;
    }

    public async ValueTask InitializeAsync()
    {
        _dockerAvailable = CheckDocker();
        if (!_dockerAvailable)
        {
            return;
        }

        // Start mock kernel HttpListener
        _kernelPort = GetFreePort();
        _kernel = new HttpListener();
        if (!OperatingSystem.IsWindows()) { _kernel.Prefixes.Add($"http://*:{_kernelPort}/"); } else { _kernel.Prefixes.Add($"http://127.0.0.1:{_kernelPort}/"); }
        _kernel.Prefixes.Add($"http://localhost:{_kernelPort}/");
        _kernel.Start();

        _cts = new CancellationTokenSource();
        _kernelLoop = Task.Run(() => RunMockKernelLoop(_kernel, _cts.Token), TestContext.Current.CancellationToken);

        // Create temporary html directory
        _tempHtmlDir = Path.Combine(Path.GetTempPath(), $"intochat-nginx-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHtmlDir);
        await File.WriteAllTextAsync(Path.Combine(_tempHtmlDir, "index.html"), "SHELL", Encoding.UTF8, TestContext.Current.CancellationToken);

        var repoRoot = FindRepoRoot();
        var templateFile = Path.Combine(repoRoot, "ui", "shell", "nginx", "default.conf.template");

        // Start docker container
        var runArgs = new List<string>
        {
            "run", "--rm", "-d", "-p", "127.0.0.1::80",
            "-e", $"INTOCHATOS_KERNEL_UPSTREAM=http://host.docker.internal:{_kernelPort}",
            "-e", "NGINX_ENVSUBST_FILTER=^INTOCHATOS_KERNEL_UPSTREAM$",
            "--mount", $"type=bind,source={_tempHtmlDir.Replace('\\', '/')},target=/usr/share/nginx/html,readonly",
            "--mount", $"type=bind,source={templateFile.Replace('\\', '/')},target=/etc/nginx/templates/default.conf.template,readonly",
        };

        if (OperatingSystem.IsLinux())
        {
            runArgs.AddRange(["--add-host", "host.docker.internal:host-gateway"]);
        }

        runArgs.Add("nginx:1.27-alpine");

        var runResult = RunCommand("docker", [.. runArgs]);
        if (runResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to run nginx docker container: {runResult.Error}");
        }

        _containerId = runResult.Output.Trim();

        // Get mapped port
        var portResult = RunCommand("docker", ["port", _containerId, "80/tcp"]);
        if (portResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to get port for container {_containerId}: {portResult.Error}");
        }

        var endpoint = portResult.Output.Trim(); // e.g. 127.0.0.1:49153
        _proxyBaseUrl = $"http://{endpoint}";

        // Wait until nginx is healthy and serving
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var ready = false;
        for (var i = 0; i < 60; i++)
        {
            try
            {
                var response = await client.GetAsync(_proxyBaseUrl, TestContext.Current.CancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    ready = true;
                    break;
                }
            }
            catch
            {
                // Wait and retry
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        if (!ready)
        {
            throw new InvalidOperationException($"nginx container at {_proxyBaseUrl} did not become ready in time.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_containerId != null)
        {
            RunCommand("docker", ["stop", _containerId]);
        }

        _cts?.Cancel();
        if (_kernelLoop != null)
        {
            try
            {
                await _kernelLoop;
            }
            catch
            {
                // ignore
            }
        }

        _kernel?.Close();

        if (_tempHtmlDir != null && Directory.Exists(_tempHtmlDir))
        {
            try
            {
                Directory.Delete(_tempHtmlDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public async Task RootStaysStatic()
    {
        if (!_dockerAvailable)
        {
            return;
        }

        using var client = new HttpClient();
        var response = await client.GetStringAsync(_proxyBaseUrl, TestContext.Current.CancellationToken);
        Assert.Equal("SHELL", response);
    }

    [Theory]
    [InlineData("/auth/handoff?t=QUERY-CANARY")]
    [InlineData("/identity/session")]
    [InlineData("/portal/assets/auth.js")]
    [InlineData("/neurons.Neurons/Call")]
    public async Task AuthIdentityPortalAndTransportShareHostAndCookie(string path)
    {
        if (!_dockerAvailable)
        {
            return;
        }

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_proxyBaseUrl}{path}");
        request.Headers.Host = "os.example.test";
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Cookie", "session=proof");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(path, root.GetProperty("path").GetString());
        Assert.Equal($"host.docker.internal:{_kernelPort}", root.GetProperty("host").GetString());
        Assert.Equal("os.example.test", root.GetProperty("forwardedHost").GetString());
        Assert.Equal("https", root.GetProperty("scheme").GetString());
        Assert.Equal("session=proof", root.GetProperty("cookie").GetString());

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookies));
        Assert.Contains(setCookies, c => c.Contains("HttpOnly"));
    }

    [Fact]
    public async Task ProxyLogsNeverContainQueryTickets()
    {
        if (!_dockerAvailable)
        {
            return;
        }

        using var client = new HttpClient();
        await client.GetAsync($"{_proxyBaseUrl}/auth/handoff?t=QUERY-CANARY", TestContext.Current.CancellationToken);

        var logs = RunCommand("docker", ["logs", _containerId!]).Output;
        Assert.DoesNotContain("QUERY-CANARY", logs);
    }

    [Theory]
    [InlineData("GET", null)]
    [InlineData("POST", "request-proof")]
    public async Task OrdinaryRequestsOmitUpstreamConnectionEvenWhenClientCloses(string method, string? body)
    {
        if (!_dockerAvailable)
        {
            return;
        }

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"{_proxyBaseUrl}/transport/headers");
        request.Headers.ConnectionClose = true;
        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("connection").ValueKind);
        Assert.Equal(method, root.GetProperty("method").GetString());
        Assert.Equal(body ?? "", root.GetProperty("body").GetString());
    }

    [Fact]
    public async Task LargeChunkedResponseIsDeliveredCompletely()
    {
        if (!_dockerAvailable)
        {
            return;
        }

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_proxyBaseUrl}/neurons.Neurons/Large");
        request.Content = new StringContent("call", Encoding.UTF8);
        request.Headers.ConnectionClose = true;

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Null(response.Content.Headers.ContentLength);

        var data = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(LargeBody, data);
    }

    [Fact]
    public async Task WebSocketUpgradeReachesUpstreamAndReturnsSwitchingProtocols()
    {
        if (!_dockerAvailable)
        {
            return;
        }

        var wsUri = new Uri($"ws://{_proxyBaseUrl!.Replace("http://", "")}/transport/websocket");
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await ws.ConnectAsync(wsUri, cts.Token);
        Assert.Equal(WebSocketState.Open, ws.State);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    private static void RunMockKernelLoop(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var contextTask = listener.GetContextAsync();
                contextTask.Wait(ct);
                var context = contextTask.Result;
                _ = Task.Run(() => HandleKernelRequest(context), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private static async Task HandleKernelRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        if (req.IsWebSocketRequest && req.RawUrl?.StartsWith("/transport/websocket", StringComparison.Ordinal) == true)
        {
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            var buffer = new byte[1024];
            try
            {
                while (wsContext.WebSocket.State == WebSocketState.Open)
                {
                    var result = await wsContext.WebSocket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await wsContext.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                        break;
                    }
                }
            }
            catch
            {
                // client closed
            }
            return;
        }

        string requestBody;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
        {
            requestBody = await reader.ReadToEndAsync();
        }

        if (req.RawUrl == "/neurons.Neurons/Large")
        {
            res.StatusCode = 200;
            res.ContentType = "application/octet-stream";
            res.SendChunked = true;
            using (var output = res.OutputStream)
            {
                const int chunkSize = 4096;
                for (var offset = 0; offset < LargeBody.Length; offset += chunkSize)
                {
                    var count = Math.Min(chunkSize, LargeBody.Length - offset);
                    await output.WriteAsync(LargeBody.AsMemory(offset, count));
                    await output.FlushAsync();
                }
            }
            res.Close();
            return;
        }

        byte[] bodyBytes;
        if (req.RawUrl == "/transport/headers")
        {
            var data = new
            {
                connection = req.Headers["Connection"],
                method = req.HttpMethod,
                body = requestBody
            };
            bodyBytes = JsonSerializer.SerializeToUtf8Bytes(data);
        }
        else
        {
            var data = new
            {
                path = req.RawUrl,
                host = req.Headers["Host"],
                forwardedHost = req.Headers["X-Forwarded-Host"],
                scheme = req.Headers["X-Forwarded-Proto"],
                cookie = req.Headers["Cookie"]
            };
            bodyBytes = JsonSerializer.SerializeToUtf8Bytes(data);
        }

        res.StatusCode = 200;
        res.ContentType = "application/json";
        res.ContentLength64 = bodyBytes.Length;
        res.Headers.Add("Set-Cookie", "session=proof; HttpOnly; Secure; SameSite=Lax; Path=/");
        await res.OutputStream.WriteAsync(bodyBytes);
        res.Close();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool CheckDocker()
    {
        try
        {
            var result = RunCommand("docker", ["info"]);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Could not find Intochat.OS.slnx");
    }

    private static (int ExitCode, string Output, string Error) RunCommand(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi)!;
        var outText = proc.StandardOutput.ReadToEnd();
        var errText = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, outText, errText);
    }
}
