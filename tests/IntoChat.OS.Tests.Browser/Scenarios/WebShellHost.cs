using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using DigitalBrain.Brains;
using DigitalBrain.Modules.Configuration;
using IntoChat.Aspire.Screens;
using IntoChat.OS.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Yarp.ReverseProxy.Transforms;
using IntoChatOSNames = IntoChat.OS.Host.Configuration.IntoChatOSNames;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Browser.Scenarios;

// The product under Aspire's testing builder (the kernel only, no shell resource, no containers) and the released web bundle served
// on the kernel's origin, as a deployment serves it: one loopback origin answers the bundle's static files and its config.json and
// forwards everything else to the kernel with the browser's Host kept. The presence exchange is same-origin only, so this is what
// lets the shell sign in with the presence token. Test tier only: the production host and FlutterHostOptions are unchanged.
public sealed class WebShellHost(WarmBrowserFixture browser) : IAsyncLifetime
{
    private static readonly string[] DisabledModules = ["artcraft", "playwright", "postgres", "qdrant", "clickhouse", "supabase", "azure", "roslyn", "sandbox", "webtools"];
    private readonly TestHome _home = new();
    private IDistributedApplicationTestingBuilder? _builder;
    private WebApplication? _origin;
    private string _presence = "";
    private int _brainCounter;

    public DistributedApplication App { get; private set; } = null!;

    public string Origin { get; private set; } = "";

    public string Scope { get; } = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    public string NewBrain(string prefix = "browser")
        => $"{prefix}{Interlocked.Increment(ref _brainCounter)}";

    public Task<IBrowserContext> NewBrowserContextAsync(BrowserNewContextOptions? options = null, CancellationToken ct = default)
        => browser.NewContextAsync(options, ct);

    public Task<IBrowserContext> NewBrowserContextAsync(int width, int height, CancellationToken ct = default)
        => browser.NewContextAsync(width, height, ct);

    public HttpClient CreateKernelClient()
    {
        var client = App.CreateHttpClient("os", "http");
        client.DefaultRequestHeaders.TryAddWithoutValidation(PresenceProof.HeaderName, _presence);
        return client;
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            var bundle = await WebBundle.Ensure(TestHome.RepoRoot(), "apps/gallery/view");
            IntoChat.OS.Host.Modules.OsModule.InitializeConfigurationRoot();
            var originPort = FreePort();
            Origin = $"http://127.0.0.1:{originPort}";
            var aspireDashboardUrl = $"{Origin}/aspire-dashboard-stub";
            List<string> args =
            [
                $"--{IntoChatOSNames.HomeKey}={_home.Path}",
                $"--{IntoChatOSNames.TopologyKey}={IntoChatOSNames.TopologyLocal}",
                $"--{IntoChatOSNames.ShellEnabledKey}=false",
                $"--{IntoChatOSNames.FrontEnabledKey}=false",
                $"--{ModuleConfiguration.ModuleSection("devices")}:ReplaceOptions=true",
                $"--{ModuleConfiguration.ModuleSection("devices")}:Options:FoundryLocal:Enabled=false",
                $"--{AspireAppState.ConfiguredUrlKey}={aspireDashboardUrl}",
            ];
            args.AddRange(DisabledModules.Select(module => $"--{ModuleConfiguration.ModuleSection(module)}:Enabled=false"));

            _builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.IntoChat_OS_AppHost>([.. args]);
            if (_builder.Resources.OfType<ProjectResource>().FirstOrDefault(r => r.Name == "os") is { } kernelResource)
            {
                _builder.CreateResourceBuilder(kernelResource)
                    .WithArgs($"--{AspireAppState.ConfiguredUrlKey}={aspireDashboardUrl}");
            }
            App = await _builder.BuildAsync();
            using var start = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await App.StartAsync(start.Token);
            await App.ResourceNotifications.WaitForResourceHealthyAsync("os", start.Token);
            _presence = await TestHome.ReadPresenceTokenAsync(_home.PresenceFilePath, start.Token, TimeSpan.FromMinutes(2));
            var kernel = App.GetEndpoint("os", "http").ToString().TrimEnd('/');
            await StartOrigin(bundle, kernel);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    // The shell with semantics on and the presence token in the fragment, as the brain browser opens it.
    public string Shell => $"{Origin}/?semantics=true#presence={Uri.EscapeDataString(_presence)}";

    // Opens a specific app view root (e.g. "apps/gallery/view") on the same web bundle and kernel.
    public string ShellFor(string root) => $"{Origin}/?root={Uri.EscapeDataString(root)}&semantics=true#presence={Uri.EscapeDataString(_presence)}";

    private async Task StartOrigin(string bundle, string kernel)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(Origin);
        builder.Logging.ClearProviders();
        builder.Services.AddHttpForwarder();
        var app = builder.Build();
        var config = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["INTOCHATOS_UI_BASE"] = Origin,
            ["INTOCHATOS_SHELL"] = "desk",
            ["INTOCHATOS_CHAT"] = "main",
        });
        // The bundle ships a placeholder config.json: answer this origin's own first.
        app.Use((context, next) => context.Request.Path == "/config.json"
            ? Results.Text(config, "application/json").ExecuteAsync(context)
            : next(context));
        app.Use((context, next) => context.Request.Path.StartsWithSegments("/aspire-dashboard-stub", StringComparison.OrdinalIgnoreCase)
            ? Results.Text(
                $"<!doctype html><html><head><title>.NET Aspire Dashboard</title></head><body><h1>.NET Aspire Dashboard</h1><p>{WebUtility.HtmlEncode(context.Request.Path.Value)}</p></body></html>",
                "text/html").ExecuteAsync(context)
            : next(context));
        // Static files run before routing: the forwarder's catch-all route would otherwise claim every path first.
        var files = new PhysicalFileProvider(bundle);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ServeUnknownFileTypes = true });
        app.UseRouting();
        // Keep the browser's Host: the kernel's same-origin check compares it with the Origin header.
        app.MapForwarder("/{**catch-all}", kernel, transforms => transforms.AddOriginalHost(true));
        await app.StartAsync();
        _origin = app;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_origin is not null) { try { await _origin.DisposeAsync(); } catch { /* best effort */ } }
        if (App is not null)
        {
            try { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await App.StopAsync(stop.Token); } catch { /* best effort */ }
            try { await App.DisposeAsync(); } catch { /* best effort */ }
        }
        if (_builder is IAsyncDisposable disposable) { try { await disposable.DisposeAsync(); } catch { /* best effort */ } }
        _home.Dispose();
    }
}

// The released web bundle, built once per input change into ui/build/web-scenarios (never the shell's own build/web): it carries a
// stamp of the hash of the shell's sources and defines, and a bundle whose stamp matches is reused. The bundle mounts one app's
// top-level view as its fixed root (INTOCHATOS_ROOT, the connector root the workspace already supports), so a scenario starts on the
// app's own view instead of going through the desktop.
public static class WebBundle
{
    private const string StampName = ".scenario-inputs";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<string> Ensure(string repoRoot, string root)
    {
        var ui = Path.Combine(repoRoot, "ui");
        var output = Path.Combine(ui, "build", "web-scenarios");
        var stamp = Path.Combine(output, StampName);
        await Gate.WaitAsync();
        try
        {
            var defines = $"--dart-define=INTOCHATOS_ROOT={root} --dart-define=INTOCHATOS_ROOT_KIND=view";
            var hash = InputsHash(ui) + "|" + defines;
            if (File.Exists(stamp) && await File.ReadAllTextAsync(stamp) == hash && File.Exists(Path.Combine(output, "main.dart.js"))) { return output; }
            // flutter is a batch file on Windows: run it through the command interpreter so it resolves on PATH as in a shell.
            var (command, arguments) = OperatingSystem.IsWindows()
                ? ("cmd.exe", $"/d /c flutter build web --release --no-pub --output build/web-scenarios {defines}")
                : ("flutter", $"build web --release --no-pub --output build/web-scenarios {defines}");
            using var build = Process.Start(new ProcessStartInfo(command, arguments)
            {
                WorkingDirectory = ui,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("flutter did not start.");
            var stdout = build.StandardOutput.ReadToEndAsync();
            var stderr = build.StandardError.ReadToEndAsync();
            await build.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
            if (build.ExitCode != 0) { throw new InvalidOperationException($"flutter build web failed ({build.ExitCode}): {await stderr}\n{await stdout}"); }
            await File.WriteAllTextAsync(stamp, hash);
            return output;
        }
        finally { Gate.Release(); }
    }

    private static string InputsHash(string ui)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IEnumerable<string> files = [.. Directory.EnumerateFiles(Path.Combine(ui, "lib"), "*", SearchOption.AllDirectories),
            .. Directory.EnumerateFiles(Path.Combine(ui, "web"), "*", SearchOption.AllDirectories),
            .. new[] { "pubspec.yaml", "pubspec.lock" }.Select(name => Path.Combine(ui, name)).Where(File.Exists)];
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(ui, file).Replace('\\', '/')));
            sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
