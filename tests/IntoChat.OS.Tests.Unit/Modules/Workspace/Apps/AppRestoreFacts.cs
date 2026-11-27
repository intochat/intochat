using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppRestoreFacts
{
    private sealed class StubHostedAppTypes(params string[] hostedNames) : IHostedAppTypes
    {
        private readonly HashSet<string> _hosted = new(hostedNames, StringComparer.Ordinal);
        public bool IsHostLoaded(string appKey) => _hosted.Contains(appKey);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    // The unit host has no program runtime, so every deploy throws: the restore after activation fails, and the first caller of
    // the reactivated app still gets its answer, with the failure on the activity.
    [Fact]
    public async Task RestoreFailureNeverSurfacesToTheFirstCaller()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();
        var id = new NeuronId(new BrainId("owner"), IntoChat.OS.Host.Primitives.AppGrainType.Name, IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId("owner"), "notes").Key);
        var app = os.Get<IApp>(id);
        var bundle = new AppBundle("notes", "1.0.0", new AppManifest("Notes", "Keep notes", [], [], []), "spec", [], "Missing.Notes, Missing");
        try { await app.Import(bundle); }
        catch (Exception) { /* the first deploy fails here too; the record is what the reactivation restores */ }

        await os.Deactivate(id);
        var read = await app.Read();

        Assert.Equal("Notes", read.Manifest.Title);
        await os.Idle();
        // The restore is a one-way turn of its own: it lands after the first call, not inside it.
        var after = read;
        for (var attempt = 0; attempt < 100 && !after.Activity.Any(line => line.StartsWith("restore failed:", StringComparison.Ordinal)); attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            after = await app.Read();
        }
        Assert.Contains(after.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetiredUneditedOsShippedApp_RestoresCleanlyWithoutError()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var logger = new RecordingLogger<AppNeuron>();
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHostedAppTypes>(new StubHostedAppTypes("ino", "settings"));
        services.AddSingleton<ILogger<AppNeuron>>(logger);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();

        var id = new NeuronId(new BrainId("owner"), IntoChat.OS.Host.Primitives.AppGrainType.Name, IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId("owner"), "uikit").Key);
        var app = os.Get<IApp>(id);
        var bundle = new AppBundle("uikit", "1.0.0", new AppManifest("UI Kit", "Retired UI Kit", [], [], []), "spec", [], "IntoChat.OS.Apps.UiKit.UiKitApp, IntoChat.OS.Apps");
        var origin = new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "uikit", "1.0.0");
        try { await app.Import(bundle, origin); }
        catch (Exception) { /* persisted state before retirement */ }

        logger.Entries.Clear();
        await os.Deactivate(id);
        var read = await app.Read();
        await os.Idle();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var after = await app.Read();

        Assert.Equal("UI Kit", read.Manifest.Title);
        Assert.DoesNotContain(after.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Error);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("retired shipped app", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ActiveShippedRootOrEditedOsApp_WithMissingType_StillFailsRestoreLoudly()
    {
        DigitalBrain.Invocation.RuntimeProtocol.Register(CatalogContractsJson.Default);
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var logger = new RecordingLogger<AppNeuron>();
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHostedAppTypes>(new StubHostedAppTypes("settings"));
        services.AddSingleton<ILogger<AppNeuron>>(logger);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();

        // 1. Active shipped root app ("ino") with missing compiled type must still fail restore loudly.
        var rootId = new NeuronId(new BrainId("owner"), IntoChat.OS.Host.Primitives.AppGrainType.Name, IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId("owner"), ShippedApps.Ino).Key);
        var rootApp = os.Get<IApp>(rootId);
        var rootBundle = new AppBundle(ShippedApps.Ino, "1.0.0", new AppManifest("Ino", "Root agent", [], [], []), "spec", [], "Missing.InoApp, Missing");
        var rootOrigin = new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), ShippedApps.Ino, "1.0.0");
        try { await rootApp.Import(rootBundle, rootOrigin); }
        catch (Exception) { /* persisted state */ }

        logger.Entries.Clear();
        await os.Deactivate(rootId);
        _ = await rootApp.Read();
        await os.Idle();

        var afterRoot = await rootApp.Read();
        for (var attempt = 0; attempt < 100 && !afterRoot.Activity.Any(line => line.StartsWith("restore failed:", StringComparison.Ordinal)); attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            afterRoot = await rootApp.Read();
        }
        Assert.Contains(afterRoot.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);

        // 2. User-edited os-origin app with missing compiled type must still fail restore loudly.
        var editedId = new NeuronId(new BrainId("owner"), IntoChat.OS.Host.Primitives.AppGrainType.Name, IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId("owner"), "uikit").Key);
        var editedApp = os.Get<IApp>(editedId);
        var editedBundle = new AppBundle("uikit", "1.0.0", new AppManifest("UI Kit", "Edited copy", [], [], []), "spec", [], "IntoChat.OS.Apps.UiKit.UiKitApp, IntoChat.OS.Apps");
        var editedOrigin = new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "uikit", "1.0.0");
        try { await editedApp.Import(editedBundle, editedOrigin); }
        catch (Exception) { /* persisted state */ }
        try { await editedApp.Rollback("1.0.0"); }
        catch (Exception) { /* rollback marks Edited = true before Deploy() throws */ }

        logger.Entries.Clear();
        await os.Deactivate(editedId);
        _ = await editedApp.Read();
        await os.Idle();

        var afterEdited = await editedApp.Read();
        for (var attempt = 0; attempt < 100 && !afterEdited.Activity.Any(line => line.StartsWith("restore failed:", StringComparison.Ordinal)); attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            afterEdited = await editedApp.Read();
        }
        Assert.Contains(afterEdited.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }
}
