using DigitalBrain.Apps;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Text;

namespace DigitalBrain.OS;

// One deployment migration, not a runtime coordinator. Legacy child neurons retain their keys;
// the archived app records keep custom programs, receipts and publication history recoverable.
internal sealed class OsInoMigration(IGrainFactory grains, FeatureCatalog catalog, IServiceProvider services) : IFeatureMigration
{
    private const string Migration = "ino-v3";
    private static readonly Guid SelectionOperation = new("f8c296ed-8701-4d8a-9ce3-4838308434fb");

    public async Task<bool> Required(string brain, string name, AppSnapshot current, CancellationToken ct)
    {
        if (name != "ino" || current.Settings.GetValueOrDefault("CompletedAppMigration") == Migration) { return false; }
        var source = await grains.GetGrain<IApp>(IApp.Key(brain, "assistant")).Read().WaitAsync(ct);
        return IsLegacy(source) && await catalog.Find("os", "ino") is { } bundle && Version.Parse(bundle.Version).Major >= 3;
    }

    public async Task PrepareBrain(string brain, CancellationToken ct)
    {
        var current = await grains.GetGrain<IApp>(IApp.Key(brain, "ino")).Read().WaitAsync(ct);
        if (!await Required(brain, "ino", current, ct)) { return; }
        var bundle = await catalog.Find("os", "ino") ?? throw new InvalidOperationException("The Ino migration bundle is unavailable.");
        await services.GetRequiredService<FeatureLoader>().Load(brain, "ino", bundle, new("os", new AppSource.Shipped(), "ino", bundle.Version), ct);
    }

    public async Task<AppBundle> BeforeLoad(string brain, string name, AppBundle bundle, CancellationToken ct)
    {
        if (name != "ino" || Version.Parse(bundle.Version).Major < 3) { return bundle; }
        var sourceKey = IApp.Key(brain, "assistant");
        var targetKey = IApp.Key(brain, "ino");
        var source = grains.GetGrain<IApp>(sourceKey);
        var target = grains.GetGrain<IApp>(targetKey);
        var current = await target.Read().WaitAsync(ct);
        if (current.Settings.GetValueOrDefault("CompletedAppMigration") == Migration)
        {
            var preserved = new Dictionary<string, string>(bundle.Files, StringComparer.Ordinal);
            foreach (var (path, text) in current.Files.Where(file => !file.Key.EndsWith(".cs", StringComparison.Ordinal))) { preserved[path] = text; }
            return bundle with { Files = preserved };
        }
        if (!IsLegacy(await source.Read().WaitAsync(ct))) { return bundle; }
        if (current.Manifest.Title.Length > 0 && current.Origin is not { Publisher: "os", Name: "ino", Source: AppSource.Shipped })
        { throw new InvalidOperationException("The existing Ino app is not the shipped Ino; its data was left intact."); }
        if (current.Manifest.Title.Length == 0)
        {
            await target.Create(bundle.Manifest, bundle.Spec).WaitAsync(ct);
            await target.RecordOrigin(new("os", new AppSource.Shipped(), "ino", bundle.Version)).WaitAsync(ct);
        }
        var old = grains.GetGrain<IAppMigration>(sourceKey);
        var destination = grains.GetGrain<IAppMigration>(targetKey);
        await old.Pause(Migration).WaitAsync(ct);
        await destination.Pause(Migration).WaitAsync(ct);
        await destination.Adopt(Migration, sourceKey).WaitAsync(ct);

        var pointer = await grains.GetGrain<IText>(sourceKey + "/conversation").Read().WaitAsync(ct);
        var chat = await grains.GetGrain<IChat>(sourceKey + "/transcript").Read().WaitAsync(ct);
        var composer = await grains.GetGrain<IComposer>(sourceKey + "/composer").Read().WaitAsync(ct);
        // A visible draft/audio capture already belongs to its composer's source. A legacy
        // pointer update can have raced projection; retain that captured source at cutover.
        // Once binding committed, it is authoritative even if a partially started new program
        // selected another conversation before deployment acknowledgement failed.
        var selected = chat.Selection?.Agent ?? (composer.Context.Length > 0 ? composer.Context
            : sourceKey + "/conversations/" + (pointer.Markdown.Length == 0 ? "main" : pointer.Markdown));
        var generation = chat.Selection?.Revision ?? Math.Max(0, Math.Max(pointer.Version, Math.Max(composer.Generation, chat.Projection?.Generation ?? 0)));
        if (chat.Selection is null && chat.Projection is { } projection && projection.Source != selected) { generation = checked(generation + 1); }
        if (chat.Selection is null)
        { await grains.GetGrain<IChatMigration>(sourceKey + "/transcript").AdoptAgent(selected, generation, SelectionOperation).WaitAsync(ct); }
        if (composer.FollowedChat != sourceKey + "/transcript")
        { await grains.GetGrain<IComposerMigration>(sourceKey + "/composer").AdoptFollow(sourceKey + "/transcript").WaitAsync(ct); }

        // The old Ino forwarding ids differ from the Assistant ids. Keep both sets so retained
        // completed forwarding requests cannot create a second conversation or recording.
        await old.Redirect(Migration, targetKey).WaitAsync(ct);
        var retained = await target.AsReference<IRetainsSignals>().Retained(nameof(DigitalBrain.Apps.Signals.AppRequested), 0).WaitAsync(ct);
        var replayable = retained.Select(item => item.Signal).OfType<DigitalBrain.Apps.Signals.AppRequested>().Select(request => request.RequestId).ToHashSet();
        await MergeLedger(targetKey + "/handled/new-conversation", sourceKey + "/handled/new", replayable, ct);
        await MergeLedger(targetKey + "/handled/voice", sourceKey + "/handled/voice", replayable, ct);

        var files = new Dictionary<string, string>(bundle.Files, StringComparer.Ordinal);
        var oldState = await old.Archive(Migration).WaitAsync(ct);
        var inoState = await destination.Archive(Migration).WaitAsync(ct);
        foreach (var (path, text) in oldState.Files.Where(file => !file.Key.EndsWith(".cs", StringComparison.Ordinal) && file.Key != "icon.svg")) { files[path] = text; }
        foreach (var (path, text) in inoState.Files.Where(file => !file.Key.EndsWith(".cs", StringComparison.Ordinal))) { files[path] = text; }
        return bundle with { Files = files };
    }

    public async Task AfterLoad(string brain, string name, AppBundle bundle, CancellationToken ct)
    {
        if (name != "ino") { return; }
        var key = IApp.Key(brain, name);
        var current = await grains.GetGrain<IApp>(key).Read().WaitAsync(ct);
        if (current.Settings.ContainsKey("ChatDataApp") && current.Settings.GetValueOrDefault("CompletedAppMigration") != Migration)
        { await grains.GetGrain<IAppMigration>(key).Resume(Migration).WaitAsync(ct); }
    }

    private async Task MergeLedger(string from, string to, HashSet<Guid> replayable, CancellationToken ct)
    {
        var source = (await grains.GetGrain<IText>(from).Read().WaitAsync(ct)).Markdown;
        var target = grains.GetGrain<IText>(to);
        var held = (await target.Read().WaitAsync(ct)).Markdown;
        var archive = grains.GetGrain<IText>(from + "/migration-ino-v3");
        if ((await archive.Read().WaitAsync(ct)).Markdown.Length == 0)
        { await archive.Set(System.Text.Json.JsonSerializer.Serialize(new { Source = source, Target = held })).WaitAsync(ct); }
        var ids = held.Split('\n', StringSplitOptions.RemoveEmptyEntries).Concat(source.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Where(value => Guid.TryParse(value, out _)).Select(Guid.Parse).Distinct()
            .OrderBy(id => replayable.Contains(id) ? 1 : 0).Select(id => id.ToString());
        var merged = string.Join('\n', ids);
        if (merged != held) { await target.Set(merged).WaitAsync(ct); }
    }

    private static bool IsLegacy(AppSnapshot app)
        => app.Manifest.Title.Length > 0 && app.Origin is { Publisher: "os", Name: "assistant", Source: AppSource.Shipped };
}
