using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Client;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Modules.Apps;
using IntoChatOSNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

public sealed class OsAppHostFixture : IAsyncLifetime
{
    private static readonly string[] DisabledModules =
    [
        "csharp",
        "artcraft",
        "playwright",
        "postgres",
        "qdrant",
        "clickhouse",
        "supabase",
        "azure",
        "roslyn",
    ];

    private readonly TestHome _home = new();
    private readonly string _features;
    private IDistributedApplicationTestingBuilder? _builder;

    public OsAppHostFixture()
        : this(DefaultFeaturesPath())
    {
    }

    internal OsAppHostFixture(string features)
    {
        _features = features;
    }

    public DistributedApplication App { get; private set; } = null!;

    public HttpClient Os { get; private set; } = null!;

    public IntoChatOSConnection Connection { get; private set; } = null!;

    public string Home => _home.Path;

    public string Scope { get; } = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    public TimeSpan StartupDuration { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var started = DateTimeOffset.UtcNow;
        var featuresKey = IntoChatOSConfiguration.ModuleOptionsSection("apps") + ":Features";
        var args = new List<string>
        {
            $"--{IntoChatOSNames.HomeKey}={Home}",
            $"--{IntoChatOSNames.TopologyKey}={IntoChatOSNames.TopologyLocal}",
            $"--{IntoChatOSNames.ShellEnabledKey}=false",
            $"--{IntoChatOSNames.FrontEnabledKey}=false",
            $"--{featuresKey}={_features}",
        };
        foreach (var module in DisabledModules)
        {
            args.Add($"--{IntoChatOSConfiguration.ModuleSection(module)}:Enabled=false");
        }

        try
        {
            _builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.IntoChat_OS_AppHost>([.. args]);
            App = await _builder.BuildAsync();
            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await App.StartAsync(startTimeout.Token);
            Os = App.CreateHttpClient("OperatingSystem-a", "http");
            await Healthy(startTimeout.Token);
            Connection = await IntoChatOSConnection.OpenAsync(
                new Uri(Os.BaseAddress!, $"/brains/{Scope}/"),
                "local-session",
                $"{Scope}/apps/settings",
                cancellationToken: startTimeout.Token);
            StartupDuration = DateTimeOffset.UtcNow - started;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task Healthy(CancellationToken ct)
    {
        await App.ResourceNotifications.WaitForResourceHealthyAsync("OperatingSystem-a", ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var expectedRoots = ShippedApps.Roots.Where(root => !string.Equals(root, "circleroom", StringComparison.OrdinalIgnoreCase)).ToArray();

        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                using var health = await Os.GetAsync("/health", deadline.Token);
                if (health.IsSuccessStatusCode)
                {
                    using var appsResponse = await Os.GetAsync($"/brains/{Scope}/apps", deadline.Token);
                    if (appsResponse.IsSuccessStatusCode)
                    {
                        using var doc = await JsonDocument.ParseAsync(await appsResponse.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
                        var names = doc.RootElement.EnumerateArray()
                            .Select(item => item.GetProperty("name").GetString() ?? "")
                            .ToHashSet(StringComparer.Ordinal);
                        if (expectedRoots.All(names.Contains))
                        {
                            return;
                        }
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            try
            {
                await Connection.DisposeAsync();
            }
            catch
            {
            }
        }

        try
        {
            Os?.Dispose();
        }
        catch
        {
        }

        if (App is not null)
        {
            try
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await App.StopAsync(stopTimeout.Token);
            }
            catch
            {
            }

            try
            {
                await App.DisposeAsync();
            }
            catch
            {
            }
        }

        if (_builder is IAsyncDisposable asyncDisposable)
        {
            try
            {
                await asyncDisposable.DisposeAsync();
            }
            catch
            {
            }
        }
        else if (_builder is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
            }
        }

        _home.Dispose();
    }

    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root containing Intochat.OS.slnx was not found.");
    }

    private static string DefaultFeaturesPath() => Path.Combine(RepoRoot(), "features", "os");
}
