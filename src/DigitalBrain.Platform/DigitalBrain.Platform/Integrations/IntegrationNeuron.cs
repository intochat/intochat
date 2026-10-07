using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Integrations;

[GrainType("integration.registration")]
internal sealed class IntegrationNeuron : Neuron<IntegrationState>, IIntegration
{
    internal const string KeyPrefix = "integration/";

    // The Platform's own stamp for releasing a registration to module code. ":" cannot occur in a
    // principal id (^[a-z0-9][a-z0-9-]*$), so no account can ever be this caller.
    internal static readonly CallerContext PlatformCaller = new()
    {
        PrincipalId = "integration.registration",
        AccountId = ":integrations",
        BrainId = ":integrations",
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
        AppId = "integration.registration",
    };

    private const int MaxValueLength = 16 * 1024;

    private readonly IReadOnlyList<IntegrationSchema> _definitions;
    private readonly ISecretSealer _sealer;
    private readonly ILogger<IntegrationNeuron> _logger;
    private IntegrationSchema? _definition;

    public IntegrationNeuron(
        [PersistentState("registration", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<IntegrationState> store,
        IReadOnlyList<IntegrationSchema> definitions,
        ISecretSealer sealer, ILogger<IntegrationNeuron> logger) : base(store)
    {
        _definitions = definitions;
        _sealer = sealer;
        _logger = logger;
    }

    private IntegrationSchema Definition => _definition ??= Resolve();

    public Task<IntegrationSnapshot> Read() => Task.FromResult(SnapshotOf(Snapshot));

    public async Task<IntegrationSnapshot> Configure(ConfigureIntegration request)
    {
        RefuseAppCalls();
        return await Apply(request);
    }

    public async Task<IntegrationSnapshot> SeedIfUnconfigured(ConfigureIntegration request)
    {
        RefuseAppCalls();
        // A cleared registration was an operator's decision; seeds only ever fill virgin state.
        return SnapshotOf(Snapshot).Status == IntegrationStatus.Unconfigured && Snapshot.Revision == 0
            ? await Apply(request)
            : SnapshotOf(Snapshot);
    }

    private async Task<IntegrationSnapshot> Apply(ConfigureIntegration request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = request.Values;
        if (values is null || values.Count == 0)
        {
            throw new ArgumentException("Provide at least one value.");
        }

        // Rejected before anything is stored, and without echoing what was submitted.
        if (values.Keys.Any(field => !Definition.AllFields.Contains(field, StringComparer.Ordinal)))
        {
            throw new ArgumentException($"A submitted field is not part of this integration; it accepts: {string.Join(", ", Definition.AllFields)}.");
        }

        if (values.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength))
        {
            throw new ArgumentException($"Every value must be non-empty and at most {MaxValueLength} characters.");
        }

        var next = CopyOf(Snapshot);
        foreach (var (field, value) in values)
        {
            Audit("store", field);
            if (Definition.IsSetting(field))
            {
                next.Settings[field] = value;
            }
            else
            {
                next.Secrets = [.. next.Secrets.Where(secret => secret.Name != field), _sealer.Seal(field, value)];
            }
        }

        next.Revision++;
        var snapshot = SnapshotOf(next);
        await Save(next, new IntegrationChanged(Definition.Id, snapshot.Status));
        return snapshot;
    }

    public async Task<IntegrationSnapshot> Clear(string field)
    {
        RefuseAppCalls();
        if (field is null || !Definition.AllFields.Contains(field, StringComparer.Ordinal))
        {
            throw new ArgumentException($"The field is not part of this integration; it accepts: {string.Join(", ", Definition.AllFields)}.");
        }

        if (!Has(Snapshot, field))
        {
            return SnapshotOf(Snapshot);
        }

        Audit("remove", field);
        var next = CopyOf(Snapshot);
        next.Secrets = [.. next.Secrets.Where(secret => secret.Name != field)];
        next.Settings.Remove(field);
        next.Revision++;
        var snapshot = SnapshotOf(next);
        await Save(next, new IntegrationChanged(Definition.Id, snapshot.Status));
        return snapshot;
    }

    public Task<ReleasedIntegration> Release(CallerContext caller)
    {
        // Release is unreachable from scripts ([PlatformOnly]); an app-stamped ambient context is a platform call chain, not an app caller.
        // Values leave only to trusted platform code: a module's own grain call stamped by the Platform
        // edge. Users and assistants (authenticated HTTP), installed apps (app proxy) and the scheduler
        // never qualify, so no HTTP route or script can obtain a provider credential.
        if (caller is null || !CallerContextStamper.IsTrusted(caller)
            || caller.Kind != CallerKind.Platform || caller.StampedBy != TrustedEdge.Platform)
        {
            throw new InvalidOperationException("Only trusted platform code can release a registration.");
        }

        if (SnapshotOf(Snapshot).Status != IntegrationStatus.Ready)
        {
            throw new InvalidOperationException("The registration is not fully configured.");
        }

        var values = new Dictionary<string, string>(Snapshot.Settings, StringComparer.Ordinal);
        foreach (var secret in Snapshot.Secrets)
        {
            values[secret.Name] = _sealer.Open(secret);
        }

        return Task.FromResult(new ReleasedIntegration { Values = values, Snapshot = SnapshotOf(Snapshot) });
    }

    // Scripts run under an App stamp; nothing an installed app does may touch a provider registration,
    // whatever parameters it passes. Platform HTTP paths carry User and startup work carries no stamp.
    private static void RefuseAppCalls()
    {
        if (CallerContextStamper.TryGet(out var ambient) && (ambient.Kind == CallerKind.App || ambient.StampedBy == TrustedEdge.AppProxy))
        {
            throw new InvalidOperationException("Installed apps cannot use provider registrations.");
        }
    }

    // Field names only; never a value.
    private void Audit(string operation, string field)
    {
        var actor = CallerContextStamper.TryGet(out var current) ? current : PlatformCaller;
        _logger.LogInformation("Registration {Operation} for {Provider}/{Field} requested by {Actor} in {Account}/{Brain}.",
            operation, Definition.Id, field, actor.PrincipalId, actor.AccountId, actor.BrainId);
    }

    private static bool Has(IntegrationState state, string field)
        => state.Settings.ContainsKey(field) || state.Secrets.Any(secret => secret.Name == field);

    private static IntegrationState CopyOf(IntegrationState state) => new()
    {
        Secrets = [.. state.Secrets],
        Settings = new(state.Settings, StringComparer.Ordinal),
        Revision = state.Revision,
    };

    private IntegrationSnapshot SnapshotOf(IntegrationState state)
    {
        var missing = Definition.RequiredFields.Where(field => !Has(state, field)).ToArray();
        var status = missing.Length == 0 ? IntegrationStatus.Ready
            : state.Secrets.Length == 0 && state.Settings.Count == 0 ? IntegrationStatus.Unconfigured
            : IntegrationStatus.Partial;
        return new IntegrationSnapshot { IntegrationId = Definition.Id, Status = status, MissingFields = missing, Settings = new(state.Settings, StringComparer.Ordinal), Revision = state.Revision };
    }

    private IntegrationSchema Resolve()
    {
        var key = this.GetPrimaryKeyString();
        if (key.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            var id = key[KeyPrefix.Length..];
            var found = _definitions.FirstOrDefault(definition => definition.Id == id);
            if (found is not null)
            {
                return found;
            }
        }

        throw new InvalidOperationException("No composed module declares this integration.");
    }
}
