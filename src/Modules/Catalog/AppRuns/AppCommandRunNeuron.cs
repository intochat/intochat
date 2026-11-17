namespace IntoChat.OS.Modules.Apps.AppRuns;

internal sealed record AppCommandRunState(string RunId, string App, string Command, AppCommandStatus Status, bool Handled, string? ResultJson, string? Error)
{
    public AppCommandRunState() : this("", "", "", AppCommandStatus.Running, false, null, null) { }
}

// One command to an installed sandbox app (key = run id): it announces the run, dispatches the command to the sandbox, and keeps the outcome.
[DigitalBrain.Neurons.Neuron("app.command")]
internal sealed class AppCommandRunNeuron(ISandboxRuntimeBridge sandbox) : DigitalBrain.Neurons.Neuron<AppCommandRunState>, IAppCommandRun
{
    public async Task<AppCommandRunView> Start(string app, string command, string? payloadJson)
    {
        if (State.RunId.Length > 0) { return View(); }
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        await Save(new AppCommandRunState() with { RunId = Id.Key, App = app, Command = command }, new AppCommandStarted(Id.Key, app, command));

        SandboxDispatchResult result;
        try
        {
            result = await sandbox.DispatchCommandAsync(Id.Brain.Value, app, command, payloadJson);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or KeyNotFoundException or TimeoutException)
        {
            result = new SandboxDispatchResult(false, null, null, null, error.Message);
        }
        var failed = result.Error is { Length: > 0 } || !result.Handled;
        var reason = failed ? result.Error ?? $"The app \"{app}\" did not handle \"{command}\"." : null;
        await Save(State with { Status = failed ? AppCommandStatus.Failed : AppCommandStatus.Completed, Handled = result.Handled, ResultJson = result.ResultJson, Error = reason },
            new AppCommandDone(Id.Key, !failed, result.ResultJson, reason));
        return View();
    }

    public Task<AppCommandRunView?> Read() => Task.FromResult(State.RunId.Length > 0 ? View() : null);

    private AppCommandRunView View() => new(State.RunId, State.App, State.Command, State.Status, State.Handled, State.ResultJson, State.Error);
}
