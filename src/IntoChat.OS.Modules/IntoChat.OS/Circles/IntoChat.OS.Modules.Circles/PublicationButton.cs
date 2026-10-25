using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Button.Signals;
using IntoChat.OS.Modules.Flutter.Card;
using IntoChat.OS.Modules.Flutter.Form;
using IntoChat.OS.Modules.Flutter.Layout;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Modules.Circles;

// A synchronous UI binding retains the authenticated caller. Background app reactions do not.
internal static class PublicationButton
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Command(AppPublicationRef Publication, string TargetName, Guid OperationId);

    public static async Task Handle(IGrainFactory grains, string brain, string application, string button,
        Signal signal, Func<AppPublicationRef, string, Guid, Task<AppSnapshot>> install)
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.Kind != CallerKind.User
            || caller.StampedBy == TrustedEdge.AppProxy)
        { throw new UnauthorizedAccessException("Use the publication button as a signed-in person."); }
        var app = IApp.Key(brain, application);
        if (signal is not ButtonClicked click || click.Name != app + "/" + button || click.RequestId == Guid.Empty)
        { throw new ArgumentException("This handler accepts only its publication button.", nameof(signal)); }
        var status = grains.GetGrain<IText>(app + "/status");
        try
        {
            // The owning neuron serializes captures. Retries keep the original name and release.
            var capture = grains.GetGrain<IText>(app + "/commands/" + caller.AccountId + "/" + click.RequestId.ToString("N"));
            var saved = (await capture.Read()).Markdown;
            Command command;
            if (!string.IsNullOrEmpty(saved))
            { command = JsonSerializer.Deserialize<Command>(saved, Json) ?? throw new ArgumentException("Missing command."); }
            else
            {
                var reference = JsonSerializer.Deserialize<AppPublicationRef>(click.Action, Json) ?? throw new ArgumentException("Missing publication.");
                var form = await grains.GetGrain<IForm>(app + "/forms/" + reference.Digest).Read();
                command = new(reference, form.Fields.Single(field => field.Name == "name").Value?.Trim() ?? "", click.RequestId);
                await capture.Set(JsonSerializer.Serialize(command, Json));
            }
            var commandKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { command.Publication, command.TargetName }, Json))));
            var pending = grains.GetGrain<IText>(app + "/pending/" + caller.AccountId + "/" + commandKey);
            var prior = (await pending.Read()).Markdown;
            if (!string.IsNullOrEmpty(prior))
            { command = JsonSerializer.Deserialize<Command>(prior, Json) ?? throw new ArgumentException("Missing pending command."); }
            else { await pending.Set(JsonSerializer.Serialize(command, Json)); }
            var installed = await install(command.Publication, command.TargetName, command.OperationId);
            // The reserved table slot falls back to the platform database without a user binding.
            var missing = installed.Manifest.Slots.Where(slot => !(slot.Name == "tables" && slot.Connector == "postgres")
                && (!installed.Connections.TryGetValue(slot.Name, out var connection)
                    || string.IsNullOrWhiteSpace(connection))).Select(slot => slot.Name).ToArray();
            var state = missing.Length > 0 ? "Needs setup: " + string.Join(", ", missing) : installed.Status.ToString();
            await grains.GetGrain<ICard>(app + "/installed").Set("Your copy", state + ". Connections belong to this destination.");
            List<UiChildRef> controls = [new("card", app + "/installed")];
            await AddButton("configure-installed", "Configure app", "configuration");
            await AddButton("open-installed", "Open app", null);
            var layout = grains.GetGrain<ILayout>(app + "/result");
            await layout.Set(new("list", [.. controls]), (await layout.Read()).Revision);
            await status.Set("Installed " + installed.Name + " · " + installed.Version + " · from @" + installed.Origin?.Publisher + ". " + state + ".");
            await pending.Set("");

            async Task AddButton(string id, string label, string? view)
            {
                var key = app + "/" + id;
                await grains.GetGrain<IButton>(key).SetActivation(label, JsonSerializer.Serialize(new
                { kind = "workspace", brainId = brain, application = installed.Name, view }, Json));
                controls.Add(new("button", key));
            }
        }
        catch (Exception error)
        { await status.Set("Could not install: " + error.Message.Split(['\r', '\n'])[0]); }
    }
}
