using System.Text.Json;
using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button.Signals;
using IntoChat.OS.Flutter.Settings;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Button;

[GrainType(UIVocabulary.ButtonType)]
internal sealed class ButtonNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ButtonState> store)
    : Neuron<ButtonState>(store), IButton
{
    public Task Set(string label, string action, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Label = label.Trim();
        next.Action = action.Trim();
        next.Enabled = enabled;
        next.Activation = null;
        return Save(next, new ButtonChanged(this.GetPrimaryKeyString(), next.Version));
    }

    public Task SetVisual(ControlVisual visual)
    {
        ControlVisualValidation.Check(visual);
        var next = Snapshot;
        next.Visual = visual;
        next.Version++;
        return Save(next, new ButtonChanged(this.GetPrimaryKeyString(), next.Version));
    }

    public async Task SetActivation(string label, string activationJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationJson);
        if (activationJson.Length > 32_000) { throw new ArgumentException("Activation JSON has at most 32000 characters.", nameof(activationJson)); }
        string? kind, brainId, connection;
        try
        {
            using var document = JsonDocument.Parse(activationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new ArgumentException("Activation must be a JSON object.", nameof(activationJson)); }
            // A repeated key would let a reader other than this check see another value.
            if (document.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            { throw new ArgumentException("Activation keys must be unique.", nameof(activationJson)); }
            kind = Text(document.RootElement, "kind");
            brainId = Text(document.RootElement, "brainId");
            connection = Text(document.RootElement, "connection");
        }
        catch (JsonException error) { throw new ArgumentException("Activation must be a JSON object.", nameof(activationJson), error); }
        // One click on an install, update or remove runs with the person's session, so only the Apps app the OS
        // ships may set one, and only for the button's own brain: no other app can put a misleading label on it.
        if (kind is not null && AppKinds.Contains(kind))
        {
            var scope = this.GetPrimaryKeyString().Split('/')[0];
            if (brainId != scope || !await SettingsCaller.IsShipped(ServiceProvider.GetRequiredService<IIntoChatOS>(), scope, "apps"))
            { throw new UnauthorizedAccessException("Only the Apps app sets an install, update or remove activation."); }
        }
        // Switching the account, reconnecting to a new root, or signing an existing connection in again acts on the
        // person's session in one click: only the shipped apps that own that control may set one, for this brain.
        if (SessionOwners(kind, connection) is { } owners)
        {
            var scope = this.GetPrimaryKeyString().Split('/')[0];
            var os = ServiceProvider.GetRequiredService<IIntoChatOS>();
            var ownBrain = brainId == scope || (brainId is null && string.Equals(kind, "account", StringComparison.OrdinalIgnoreCase));
            var shipped = false;
            foreach (var owner in owners) { shipped = shipped || await SettingsCaller.IsShipped(os, scope, owner); }
            if (!ownBrain || !shipped) { throw new UnauthorizedAccessException("Only the app that owns this control sets a session activation."); }
        }
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Label = label.Trim();
        next.Enabled = true;
        next.Action = "activate";
        next.Activation = activationJson;
        await Save(next, new ButtonChanged(next.Name, next.Version));
    }

    private static readonly HashSet<string> AppKinds = new(["install", "update", "remove"], StringComparer.OrdinalIgnoreCase);

    // The shipped apps that may set a session-affecting activation, or null when the activation is not one.
    private static string[]? SessionOwners(string? kind, string? connection) => kind?.ToLowerInvariant() switch
    {
        "account" => ["circle"],
        "reconnect" => ["desktop", "workspace"],
        "connections" when !string.IsNullOrEmpty(connection) => ["settings", "apps"],
        _ => null,
    };

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public Task ClickCurrent(string expectedAction)
    {
        if (!string.Equals(expectedAction, Snapshot.Action, StringComparison.Ordinal))
        { throw new InvalidOperationException("This button changed. Read it again before clicking."); }
        return Click();
    }

    public async Task Click()
    {
        var next = Snapshot;
        if (!next.Enabled || string.IsNullOrWhiteSpace(next.Action))
        {
            throw new InvalidOperationException("Button is not ready to click.");
        }

        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.ClickCount++;
        var clicked = new ButtonClicked(this.GetPrimaryKeyString(), next.Action, Guid.NewGuid());
        await Save(next, new ButtonChanged(this.GetPrimaryKeyString(), next.Version), clicked);
        await GrainFactory.GetGrain<IUiBinding>(this.GetPrimaryKeyString()).Dispatch(clicked);
    }

    [ReadOnly] public Task<ButtonState> Read() => Task.FromResult(Named(Snapshot));

    private ButtonState Named(ButtonState state) { state.Name = this.GetPrimaryKeyString(); return state; }
}
