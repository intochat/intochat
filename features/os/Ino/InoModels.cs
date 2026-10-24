using IntoChat.OS.AI.Models;
using IntoChat.OS.Flutter;
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

    // First run: the "How should Ino run?" card. Copy moves into the INT-20 strings file later.
    public const string FirstRunTitle = "How should Ino run?";
    public const string FirstRunDetail = "Ino needs a model before it can answer.";
    public const string LocalLabel = "On this computer";
    public const string LocalMissingCopy = "Ollama isn't running on this computer. Install it, start it, then retry.";
    public const string InstallOllamaLabel = "Install Ollama";
    public const string OllamaDownload = "https://ollama.com/download";
    public const string RetryLabel = "Retry";
    public const string CloudLabel = "IntoChat Cloud";
    public const string CloudSoonCopy = "Coming soon";
    public const string KeysLabel = "Add an API key";
    public const string LocalHostedCopy = "Local models aren't available in IntoChat Cloud. Add an API key or choose another model.";
    public const string SetupLocal = "setup-local";
    public const string SetupRetry = "setup-retry";
    public const string KeysButton = "/setup-keys";
    public const string InstallOllamaButton = "/install-ollama";

    // A local provider serves it (AiModel.IsLocal); the wire carries the provider's name.
    public static bool IsLocal(ModelChoice model) => model.Provider is nameof(AiProvider.Ollama) or nameof(AiProvider.FoundryLocal);

    // A local desktop run: the catalog lists a local model. Hosted profiles strip local models (WithoutLocalModels)
    // and the catalog lists an Ollama preset only when it is configured, so a hosted brain never lists one.
    public static bool LocalRun(ModelChoices choices) => choices.Models.Any(IsLocal);

    public static string? FirstLocal(ModelChoices choices) => choices.Models.FirstOrDefault(model => IsLocal(model) && model.Available)?.Id;

    public static bool IsLocalChoice(string? id)
        => id is not null && id.StartsWith("preset:", StringComparison.Ordinal) && LLMModel.FindByMarkerName(id["preset:".Length..])?.IsLocal == true;

    public static bool NoneUsable(ModelChoices choices) => !choices.Automatic.Available && !choices.Models.Any(model => model.Available);

    // Whether a send in this chat would be NoModel (see Resolve), or nothing at all is usable.
    public static bool NeedsModel(ModelChoices choices, string? chatChoice, string appSetting)
        => NoneUsable(choices) || !choices.Automatic.Available && (chatChoice == AutoChoice
            || chatChoice is null && !(appSetting.Length > 0 && choices.Models.Any(model => model.Id == appSetting && model.Available)));

    public static string KeysActivation(string brainId)
        => System.Text.Json.JsonSerializer.Serialize(new { kind = "settings", brainId, tab = "keys" });

    public static readonly string InstallOllamaActivation = System.Text.Json.JsonSerializer.Serialize(new { url = OllamaDownload });

    // The card, or null when this chat can answer. "Add an API key" and "Install Ollama" are button neurons armed
    // with shell activations (Ino.ComposeAsync), as Desktop's Settings is; the rest are view actions (setup.cs).
    public static UiNode? FirstRun(ModelChoices choices, string? chatChoice, string appSetting, string dataKey)
    {
        if (!NeedsModel(choices, chatChoice, appSetting)) { return null; }
        List<UiNode> children = [];
        if (LocalRun(choices))
        {
            if (FirstLocal(choices) is not null) { children.Add(Ui.Button(SetupLocal, LocalLabel, SetupLocal, style: UiStyle.Primary, icon: "sparkle")); }
            else
            {
                children.Add(Ui.Text("setup-local-missing", LocalMissingCopy));
                children.Add(Ui.Reference("install-ollama", new(UIVocabulary.ButtonType, dataKey + InstallOllamaButton)));
                children.Add(Ui.Button(SetupRetry, RetryLabel, SetupRetry, style: UiStyle.Ghost, icon: "refresh"));
            }
        }
        children.Add(Ui.Button("setup-cloud", CloudLabel, "setup-cloud", enabled: false, style: UiStyle.Ghost) with { Detail = CloudSoonCopy });
        children.Add(Ui.Text("setup-cloud-soon", CloudSoonCopy));
        children.Add(Ui.Reference("setup-keys", new(UIVocabulary.ButtonType, dataKey + KeysButton)));
        var detail = !LocalRun(choices) && IsLocalChoice(chatChoice) ? LocalHostedCopy : FirstRunDetail;
        return Ui.Card("first-run", FirstRunTitle, detail, [.. children]);
    }

    // Starts the turn only when the model resolved; otherwise nothing starts.
    public static async Task<AnswerOutcome> Begin(InoModelResolution resolved, Func<ModelSelection?, Task> start)
    {
        if (resolved.Outcome != InoModelOutcome.Ready)
        { return resolved.Outcome == InoModelOutcome.NoModel ? AnswerOutcome.NoModel : AnswerOutcome.KeptInDraft; }
        await start(resolved.Model);
        return AnswerOutcome.Started;
    }

    // `selected` is the shown choice id; null is Automatic. A selection that is unavailable, or no longer listed,
    // labels the menu "<name> (unavailable)" and stays the marked item; a removed one gets a disabled stand-in.
    public static UiNode Menu(ModelChoices choices, string? selected)
    {
        var auto = Ui.Button("model-auto", AutoLabel, "model:" + AutoChoice, choices.Automatic.Available,
            selected is null ? UiStyle.Primary : UiStyle.Secondary) with
        { Detail = choices.Automatic.Reason };
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
        {
            if (await catalog.Select(chatChoice) is { } chosen) { return new(InoModelOutcome.Ready, chosen); }
            return IsLocalChoice(chatChoice) && !LocalRun(await catalog.List())
                ? new(InoModelOutcome.ChatModelUnavailable, Note: LocalHostedCopy) : new(InoModelOutcome.ChatModelUnavailable);
        }
        if (appSetting.Length > 0 && await catalog.Select(appSetting) is { } setting) { return new(InoModelOutcome.Ready, setting); }
        // Inherited Auto: with nothing available it is NoModel too, the first-run card's case.
        if (!(await catalog.List()).Automatic.Available) { return new(InoModelOutcome.NoModel); }
        return appSetting.Length == 0 ? new(InoModelOutcome.Ready) : new(InoModelOutcome.Ready, Note: DefaultUnavailableCopy);
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
