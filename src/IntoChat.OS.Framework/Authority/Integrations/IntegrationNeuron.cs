using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Integrations;

[GrainType("integration.registration")]
internal sealed class IntegrationNeuron : Grain, IIntegration
{
    internal const string KeyPrefix = "integration/";


    private const int MaxValueLength = 16 * 1024;

    private readonly ICallerAmbient _callers;

    private readonly IPersistentState<IntegrationState> _store;
    private readonly IReadOnlyList<IntegrationSchema> _definitions;
    private readonly ISecretSealer _sealer;
    private readonly ILogger<IntegrationNeuron> _logger;
    private IntegrationSchema? _definition;

    public IntegrationNeuron(
        [PersistentState("registration", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<IntegrationState> store,
        IReadOnlyList<IntegrationSchema> definitions,
        ISecretSealer sealer, ILogger<IntegrationNeuron> logger, ICallerAmbient callers)
    {
        _callers = callers;
        _store = store;
        _definitions = definitions;
        _sealer = sealer;
        _logger = logger;
    }

    private IntegrationSchema Definition => _definition ??= Resolve();

    public Task<IntegrationSnapshot> Read() => Task.FromResult(SnapshotOf(_store.State));

    public async Task<IntegrationSnapshot> Configure(ConfigureIntegration request)
    {
        RefuseAppCalls();
        var values = Validate(request);
        var next = CopyOf(_store.State);
        foreach (var (field, value) in values)
        {
            Audit("store", field);
            Write(next, field, value);
            // A person owns this field now: it may not be reconciled and it may not stay cleared.
            next.PersonFields = [.. next.PersonFields.Where(owned => owned != field), field];
            next.ClearedFields = [.. next.ClearedFields.Where(cleared => cleared != field)];
        }

        return await Commit(next);
    }

    public async Task<IntegrationSnapshot> Seed(ConfigureIntegration request)
    {
        RefuseAppCalls();
        var values = Validate(request);
        var next = CopyOf(_store.State);
        var changed = false;
        foreach (var (field, value) in values)
        {
            if (!ShouldSeed(next, field, value))
            {
                continue;
            }

            Audit("seed", field);
            Write(next, field, value);
            changed = true;
        }

        // Nothing to reconcile: leave revision and storage untouched.
        return changed ? await Commit(next) : SnapshotOf(next);
    }

    // A field is seeded when it is absent, or when no person set it and the deployment's value has
    // moved. A cleared field stays cleared; a person-set field is never a candidate.
    private bool ShouldSeed(IntegrationState state, string field, string value)
    {
        if (state.ClearedFields.Contains(field, StringComparer.Ordinal))
        {
            return false;
        }

        if (!Has(state, field))
        {
            return true;
        }

        return !state.PersonFields.Contains(field, StringComparer.Ordinal) && Differs(state, field, value);
    }

    // Settings compare as plaintext; secrets are opened with the sealer and compared in the clear.
    private bool Differs(IntegrationState state, string field, string value)
    {
        if (Definition.IsSetting(field))
        {
            return state.Settings.TryGetValue(field, out var stored) && !string.Equals(stored, value, StringComparison.Ordinal);
        }

        var entry = state.Secrets.FirstOrDefault(secret => secret.Key == field);
        return entry.Value is null || !string.Equals(_sealer.Open(entry.Value), value, StringComparison.Ordinal);
    }

    private Dictionary<string, string> Validate(ConfigureIntegration request)
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

        return values;
    }

    private void Write(IntegrationState state, string field, string value)
    {
        if (Definition.IsSetting(field))
        {
            state.Settings[field] = value;
        }
        else
        {
            state.Secrets = [.. state.Secrets.Where(secret => secret.Key != field), new KeyValuePair<string, SealedSecret>(field, _sealer.Seal(field, value))];
        }
    }

    private async Task<IntegrationSnapshot> Commit(IntegrationState next)
    {
        next.Revision++;
        var snapshot = SnapshotOf(next);
        var previous = _store.State;
        _store.State = next;
        try { await _store.WriteStateAsync(); }
        catch { _store.State = previous; throw; }
        return snapshot;
    }

    public async Task<IntegrationSnapshot> Clear(string field)
    {
        RefuseAppCalls();
        if (field is null || !Definition.AllFields.Contains(field, StringComparer.Ordinal))
        {
            throw new ArgumentException($"The field is not part of this integration; it accepts: {string.Join(", ", Definition.AllFields)}.");
        }

        if (!Has(_store.State, field))
        {
            return SnapshotOf(_store.State);
        }

        Audit("remove", field);
        var next = CopyOf(_store.State);
        next.Secrets = [.. next.Secrets.Where(secret => secret.Key != field)];
        next.Settings.Remove(field);
        // A person cleared this field: a later seed must not restore it.
        next.PersonFields = [.. next.PersonFields.Where(owned => owned != field)];
        next.ClearedFields = [.. next.ClearedFields.Where(cleared => cleared != field), field];
        return await Commit(next);
    }

    public Task<ReleasedIntegration> Release()
    {
        // Release is unreachable from scripts (IIntegration is not a neuron); an app-stamped ambient context is a platform call chain, not an app caller.
        // Values leave only to trusted platform code that entered the kernel scope, read from the stamped caller. Users and
        // assistants (authenticated HTTP), installed apps (app proxy) and the scheduler never qualify, so no HTTP route or
        // script can obtain a provider credential.
        if (!CallerRules.IsKernel(_callers.Current))
        {
            throw new InvalidOperationException("Only trusted platform code can release a registration.");
        }

        if (SnapshotOf(_store.State).Status != IntegrationStatus.Ready)
        {
            throw new InvalidOperationException("The registration is not fully configured.");
        }

        var values = new Dictionary<string, string>(_store.State.Settings, StringComparer.Ordinal);
        foreach (var (name, secret) in _store.State.Secrets)
        {
            values[name] = _sealer.Open(secret);
        }

        return Task.FromResult(new ReleasedIntegration { Values = values, Snapshot = SnapshotOf(_store.State) });
    }

    // Scripts run under an App stamp; nothing an installed app does may touch a provider registration,
    // whatever parameters it passes. Platform HTTP paths carry a person and startup work enters the kernel.
    private void RefuseAppCalls()
    {
        if (_callers.Current is { } ambient && Identity.Grants.GrantRules.IsAppCaller(ambient))
        {
            throw new InvalidOperationException("Installed apps cannot use provider registrations.");
        }
    }

    // Field names only; never a value.
    private void Audit(string operation, string field)
    {
        var caller = _callers.Current;
        _logger.LogInformation("Registration {Operation} for {Provider}/{Field} requested by {Actor} in {Brain}.",
            operation, Definition.Id, field, caller?.Actor.ToString() ?? "nobody", caller?.Brain.Value ?? "-");
    }

    private static bool Has(IntegrationState state, string field)
        => state.Settings.ContainsKey(field) || state.Secrets.Any(secret => secret.Key == field);

    private static IntegrationState CopyOf(IntegrationState state) => new()
    {
        Secrets = [.. state.Secrets],
        Settings = new(state.Settings, StringComparer.Ordinal),
        Revision = state.Revision,
        PersonFields = [.. state.PersonFields],
        ClearedFields = [.. state.ClearedFields],
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
