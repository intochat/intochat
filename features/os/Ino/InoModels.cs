using IntoChat.OS.AI.Models;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Views;

// The per-chat model menu in Ino's header and the choice it stores. A chat's choice is its index entry's
// ModelChoice: null follows the app setting Model, "auto" is Automatic, anything else is a catalog choice id.
public static class InoModels
{
    public const string AutoChoice = "auto";
    public const string AutoLabel = "Auto, cheapest model with tools";
    public const string UnavailableCopy = "This model isn't available right now. Pick another in the model menu.";

    // `selected` is the effective choice id; null is Automatic.
    public static UiNode Menu(ModelChoices choices, string? selected)
    {
        var auto = Ui.Button("model-auto", AutoLabel, "model:" + AutoChoice, choices.Automatic.Available,
            selected is null ? UiStyle.Primary : UiStyle.Secondary) with { Detail = choices.Automatic.Reason };
        UiNode[] items = [auto, .. choices.Models.Select((model, i) => Ui.Button("model-" + i, model.Label, "model:" + model.Id, model.Available,
            model.Id == selected ? UiStyle.Primary : UiStyle.Secondary) with { Detail = model.Reason })];
        var label = selected is null ? "Auto" : choices.Models.FirstOrDefault(model => model.Id == selected)?.Label ?? "Auto";
        return Ui.Menu("model", label, items, icon: "sparkle");
    }

    public static string? Effective(string? chatChoice, string appSetting)
        => chatChoice is not null ? (chatChoice == AutoChoice ? null : chatChoice) : appSetting.Length > 0 ? appSetting : null;

    // Handles "model:auto" and "model:<id>" for one chat. A choice that is not available in the current list
    // changes nothing and says so on the status line; a stored choice clears that message.
    public static async Task<bool> Choose(IModelCatalog catalog, IChatIndex index, IText status, string chatId, string action)
    {
        if (!action.StartsWith("model:", StringComparison.Ordinal)) { return false; }
        var id = action["model:".Length..];
        var choices = await catalog.List();
        var available = id == AutoChoice
            ? choices.Automatic.Available
            : id.Length > 0 && choices.Models.Any(model => model.Id == id && model.Available);
        if (!available) { await status.Set(UnavailableCopy); return false; }
        if (await index.Get(chatId) is null) { await index.Create(chatId, ChatIndex.NewChat); }
        await index.SetModel(chatId, id);
        var shown = await status.Read();
        if (shown.Markdown == UnavailableCopy) { await status.TrySet("", shown.Version); }
        return true;
    }
}
