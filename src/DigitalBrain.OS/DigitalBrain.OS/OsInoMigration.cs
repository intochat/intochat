using DigitalBrain.Apps;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Framework;

namespace DigitalBrain.OS;

// One deployment migration, not a runtime coordinator. Legacy child neurons retain their keys;
// the archived app records keep custom programs, receipts and publication history recoverable.
internal sealed class OsInoMigration(IDigitalBrains brains, FeatureCatalog catalog, IServiceProvider services) : IFeatureMigration
{
    private const string Migration = "ino-v3";
    private static readonly Guid SelectionOperation = new("f8c296ed-8701-4d8a-9ce3-4838308434fb");

    public async Task<bool> Required(string brain, string name, AppSnapshot current, CancellationToken ct)
    {
        if (name != "ino" || current.Settings.GetValueOrDefault("CompletedAppMigration") == Migration) { return false; }
        var source = await brains.Open(brain).Neuron<IApp>("apps/assistant").Read().WaitAsync(ct);
        return IsLegacy(source) && await catalog.Find("os", "ino") is { } bundle && Version.Parse(bundle.Version).Major >= 3;
    }

    public async Task PrepareBrain(string brain, CancellationToken ct)
    {
        var current = await brains.Open(brain).Neuron<IApp>("apps/ino").Read().WaitAsync(ct);
        if (!await Required(brain, "ino", current, ct)) { return; }
        var bundle = await catalog.Find("os", "ino") ?? throw new InvalidOperationException("The Ino migration bundle is unavailable.");
        await services.GetRequiredService<FeatureLoader>().Load(brain, "ino", bundle, new("os", new AppSource.Shipped(), "ino", bundle.Version), ct);
    }

    public async Task<AppBundle> BeforeLoad(string brain, string name, AppBundle bundle, CancellationToken ct)
    {
        if (name != "ino" || Version.Parse(bundle.Version).Major < 3) { return bundle; }
        var sourceKey = IApp.Key(brain, "assistant");
        var targetKey = IApp.Key(brain, "ino");
        var scope = brains.Open(brain);
        var source = scope.Neuron<IApp>("apps/assistant");
        var target = scope.Neuron<IApp>("apps/ino");
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
        var old = source.AsReference<IAppMigration>();
        var destination = target.AsReference<IAppMigration>();
        await old.Pause(Migration).WaitAsync(ct);
        await destination.Pause(Migration).WaitAsync(ct);
        await destination.Adopt(Migration, sourceKey).WaitAsync(ct);

        var pointer = await scope.Neuron<IText>("apps/assistant/conversation").Read().WaitAsync(ct);
        var transcript = scope.Neuron<IChat>("apps/assistant/transcript");
        var composerNeuron = scope.Neuron<IComposer>("apps/assistant/composer");
        var chat = await transcript.Read().WaitAsync(ct);
        var composer = await composerNeuron.Read().WaitAsync(ct);
        // A visible draft/audio capture already belongs to its composer's source. A legacy
        // pointer update can have raced projection; retain that captured source at cutover.
        // Once binding committed, it is authoritative even if a partially started new program
        // selected another conversation before deployment acknowledgement failed.
        var selected = chat.Selection?.Agent ?? (composer.Context.Length > 0 ? composer.Context
            : sourceKey + "/conversations/" + (pointer.Markdown.Length == 0 ? "main" : pointer.Markdown));
        var generation = chat.Selection?.Revision ?? Math.Max(0, Math.Max(pointer.Version, Math.Max(composer.Generation, chat.Projection?.Generation ?? 0)));
        if (chat.Selection is null && chat.Projection is { } projection && projection.Source != selected) { generation = checked(generation + 1); }
        if (chat.Selection is null)
        { await transcript.AsReference<IChatMigration>().AdoptAgent(selected, generation, SelectionOperation).WaitAsync(ct); }
        if (composer.FollowedChat != sourceKey + "/transcript")
        { await composerNeuron.AsReference<IComposerMigration>().AdoptFollow(sourceKey + "/transcript").WaitAsync(ct); }

        // The old Ino forwarding ids differ from the Assistant ids. Keep both sets so retained
        // completed forwarding requests cannot create a second conversation or recording.
        await old.Redirect(Migration, targetKey).WaitAsync(ct);
        var retained = await target.AsReference<IRetainsSignals>().Retained(nameof(DigitalBrain.Apps.Signals.AppRequested), 0).WaitAsync(ct);
        var replayable = retained.Select(item => item.Signal).OfType<DigitalBrain.Apps.Signals.AppRequested>().Select(request => request.RequestId).ToHashSet();
        await MergeLedger(scope, "apps/ino/handled/new-conversation", "apps/assistant/handled/new", replayable, ct);
        await MergeLedger(scope, "apps/ino/handled/voice", "apps/assistant/handled/voice", replayable, ct);

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
        var app = brains.Open(brain).Neuron<IApp>("apps/ino");
        var current = await app.Read().WaitAsync(ct);
        if (current.Settings.ContainsKey("ChatDataApp") && current.Settings.GetValueOrDefault("CompletedAppMigration") != Migration)
        { await app.AsReference<IAppMigration>().Resume(Migration).WaitAsync(ct); }
    }

    private static async Task MergeLedger(IDigitalBrain scope, NeuronId from, NeuronId to, HashSet<Guid> replayable, CancellationToken ct)
    {
        var source = (await scope.Neuron<IText>(from).Read().WaitAsync(ct)).Markdown;
        var target = scope.Neuron<IText>(to);
        var held = (await target.Read().WaitAsync(ct)).Markdown;
        var archive = scope.Neuron<IText>(from.Name + "/migration-ino-v3");
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
