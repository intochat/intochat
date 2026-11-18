using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using DigitalBrain.Brains;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Tests.Support;
using IntoChatOSNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

public sealed class OsAppHostFixture : IAsyncLifetime
{
    private static readonly string[] DisabledModules =
    [
        "artcraft",
        "playwright",
        "postgres",
        "qdrant",
        "clickhouse",
        "supabase",
        "azure",
        "roslyn",
        "sandbox",
    ];

    private readonly TestHome _home = new();
    private readonly string[] _extra;
    private readonly TimeSpan _startTimeout;
    private IDistributedApplicationTestingBuilder? _builder;

    public OsAppHostFixture()
        : this([])
    {
    }

    // Extra command-line settings, applied after the defaults (a later setting wins).
    internal OsAppHostFixture(string[] extra, TimeSpan? startTimeout = null)
    {
        _extra = extra;
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(300);
    }

    public DistributedApplication App { get; private set; } = null!;

    public HttpClient Os { get; private set; } = null!;

    public string Home => _home.Path;

    public string Scope { get; } = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    public string PresenceToken { get; private set; } = string.Empty;

    public TimeSpan StartupDuration { get; private set; }

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void InitializeConfigurationRoot() => IntoChat.OS.Framework.Modules.OsModule.InitializeConfigurationRoot();

    public async ValueTask InitializeAsync()
    {
        InitializeConfigurationRoot();
        var started = DateTimeOffset.UtcNow;

        var args = new List<string>
        {
            $"--{IntoChatOSNames.HomeKey}={Home}",
            $"--{IntoChatOSNames.TopologyKey}={IntoChatOSNames.TopologyLocal}",
            $"--{IntoChatOSNames.ShellEnabledKey}=false",
            $"--{IntoChatOSNames.FrontEnabledKey}=false",
        };
        foreach (var module in DisabledModules)
        {
            args.Add($"--{ModuleConfiguration.ModuleSection(module)}:Enabled=false");
        }
        args.AddRange(_extra);

        try
        {
            _builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.IntoChat_OS_AppHost>([.. args]);
            App = await _builder.BuildAsync();
            using var startTimeout = new CancellationTokenSource(_startTimeout);
            await App.StartAsync(startTimeout.Token);
            PresenceToken = await TestHome.ReadPresenceTokenAsync(_home.PresenceFilePath, startTimeout.Token, _startTimeout);
            Os = App.CreateHttpClient("OperatingSystem-a", "http");
            Os.DefaultRequestHeaders.TryAddWithoutValidation(PresenceProofValidator.HeaderName, PresenceToken);
            await Healthy(startTimeout.Token);
            StartupDuration = DateTimeOffset.UtcNow - started;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public Task Healthy(CancellationToken ct) => Healthy(Scope, ct);

    public async Task Healthy(string scope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
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
                    using var appsResponse = await Os.GetAsync($"/brains/{scope}/apps", deadline.Token);
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

    internal static string RepoRoot() => TestHome.RepoRoot();
}
