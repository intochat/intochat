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
    public const string DefaultUnavailableCopy = "The default model isn't available. Using Auto.";
    public const string UnavailableSuffix = " (unavailable)";

    // `selected` is the shown choice id; null is Automatic. A selection that is unavailable, or no longer listed,
    // labels the menu "<name> (unavailable)" and stays the marked item; a removed one gets a disabled stand-in.
    public static UiNode Menu(ModelChoices choices, string? selected)
    {
        var auto = Ui.Button("model-auto", AutoLabel, "model:" + AutoChoice, choices.Automatic.Available,
            selected is null ? UiStyle.Primary : UiStyle.Secondary) with { Detail = choices.Automatic.Reason };
        UiNode[] items = [auto, .. choices.Models.Select((model, i) => Ui.Button("model-" + i, model.Label, "model:" + model.Id, model.Available,
            model.Id == selected ? UiStyle.Primary : UiStyle.Secondary) with { Detail = model.Reason })];
        string label;
        if (selected is null) { label = "Auto"; }
        else if (choices.Models.FirstOrDefault(model => model.Id == selected) is { } listed)
        { label = listed.Available ? listed.Label : listed.Label + UnavailableSuffix; }
        else
        {
            label = selected + UnavailableSuffix;
            items = [.. items, Ui.Button("model-removed", selected, "model:" + selected, false, UiStyle.Primary) with { Detail = UnavailableCopy }];
        }
        return Ui.Menu("model", label, items, icon: "sparkle");
    }

    // What the menu marks: the chat's own choice; else the app setting while it is listed and available
    // (a rejected setting falls back to Auto, see Resolve); else Auto.
    public static string? Shown(ModelChoices choices, string? chatChoice, string appSetting)
        => chatChoice is not null ? Effective(chatChoice, appSetting)
            : appSetting.Length > 0 && choices.Models.Any(model => model.Id == appSetting && model.Available) ? appSetting : null;

    // The model a turn uses. Only a chat's own choice is ever refused: an inherited app setting that is rejected
    // falls back to Auto with a note, so an upgrade never stops answering. An explicit "auto" whose Automatic is
    // unavailable is NoModel, which Task 5's first-run card takes over.
    public static async Task<InoModelResolution> Resolve(IModelCatalog catalog, string? chatChoice, string appSetting)
    {
        if (chatChoice == AutoChoice)
        { return (await catalog.List()).Automatic.Available ? new(InoModelOutcome.Ready) : new(InoModelOutcome.NoModel); }
        if (chatChoice is not null)
        { return await catalog.Select(chatChoice) is { } chosen ? new(InoModelOutcome.Ready, chosen) : new(InoModelOutcome.ChatModelUnavailable); }
        if (appSetting.Length == 0) { return new(InoModelOutcome.Ready); }
        return await catalog.Select(appSetting) is { } setting
            ? new(InoModelOutcome.Ready, setting)
            : new(InoModelOutcome.Ready, Note: DefaultUnavailableCopy);
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

public enum InoModelOutcome { Ready, ChatModelUnavailable, NoModel }

public sealed record InoModelResolution(InoModelOutcome Outcome, ModelSelection? Model = null, string? Note = null);
