namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("shell.control")]
public sealed class ShellControlNeuron : DigitalBrain.Neuron<ShellStateSnapshot>, IShellControl
{
    public Task<ShellStateSnapshot> GetState() => Task.FromResult(State);

    public async Task<InoToolResult> ReportState(ShellStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            await Save(state, new ShellStateChanged());
            return new InoToolResult("Reported shell state.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InoToolResult($"Could not report shell state: {ex.Message}", Ok: false);
        }
    }

    public async Task<InoToolResult> ToggleWidgetBar(bool? open = null)
    {
        var targetState = open.HasValue ? (open.Value ? "open" : "close") : "toggle";
        try
        {
            var nextOpen = open ?? !State.WidgetsMenuOpen;
            await Save(State with { WidgetsMenuOpen = nextOpen }, new ShellActionSignal("toggle_widget_bar", Payload: targetState));
            return new InoToolResult($"Widget bar set to {targetState}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InoToolResult($"Could not toggle widget bar: {ex.Message}", Ok: false);
        }
    }

    public async Task<InoToolResult> OpenCard(string cardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        try
        {
            var cards = State.OpenCardIds.Contains(cardId) ? State.OpenCardIds : [.. State.OpenCardIds, cardId];
            await Save(State with { OpenCardIds = cards }, new ShellActionSignal("open_card", Target: cardId));
            return new InoToolResult($"Opened card '{cardId}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InoToolResult($"Could not open card: {ex.Message}", Ok: false);
        }
    }

    public async Task<InoToolResult> CloseCard(string cardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        try
        {
            var cards = State.OpenCardIds.Where(id => id != cardId).ToArray();
            await Save(State with { OpenCardIds = cards }, new ShellActionSignal("close_card", Target: cardId));
            return new InoToolResult($"Closed card '{cardId}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InoToolResult($"Could not close card: {ex.Message}", Ok: false);
        }
    }

    public async Task<InoToolResult> AutoArrangeCards()
    {
        try
        {
            await Save(State, new ShellActionSignal("auto_arrange"));
            return new InoToolResult("Requested auto-arrange of whiteboard cards.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InoToolResult($"Could not auto-arrange cards: {ex.Message}", Ok: false);
        }
    }
}
