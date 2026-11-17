namespace IntoChat.OS.Modules.Apps.AppRuns;

internal sealed record AppRunState(string RunId, string App, AppTrigger Trigger, string Name, AppRunStatus Status, bool Handled, string? ResultJson, string? Error)
{
    public AppRunState() : this("", "", AppTrigger.Command, "", AppRunStatus.Running, false, null, null) { }
}

// One run of an installed sandbox app (key = run id): it announces the run, dispatches the command or schedule tick to the sandbox,
// and keeps the outcome.
[DigitalBrain.Neurons.Neuron("app.run")]
internal sealed class AppRunNeuron(ISandboxRuntimeBridge sandbox) : DigitalBrain.Neurons.Neuron<AppRunState>, IAppRun
{
    public async Task<AppRunView> Start(AppTrigger trigger, string app, string name, string? payloadJson)
    {
        if (State.RunId.Length > 0) { return View(); }
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await Save(new AppRunState() with { RunId = Id.Key, App = app, Trigger = trigger, Name = name }, new AppRunStarted(Id.Key, app, trigger, name));

        SandboxDispatchResult result;
        try
        {
            result = trigger == AppTrigger.Schedule
                ? await sandbox.DispatchScheduleAsync(Id.Brain.Value, app, name)
                : await sandbox.DispatchCommandAsync(Id.Brain.Value, app, name, payloadJson);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or KeyNotFoundException or TimeoutException)
        {
            result = new SandboxDispatchResult(false, null, null, null, error.Message);
        }
        var failed = result.Error is { Length: > 0 } || !result.Handled;
        var reason = failed ? result.Error ?? $"The app \"{app}\" did not handle \"{name}\"." : null;
        await Save(State with { Status = failed ? AppRunStatus.Failed : AppRunStatus.Completed, Handled = result.Handled, ResultJson = result.ResultJson, Error = reason },
            new AppRunDone(Id.Key, !failed, result.ResultJson, reason));
        return View();
    }

    public Task<AppRunView?> Read() => Task.FromResult(State.RunId.Length > 0 ? View() : null);

    private AppRunView View() => new(State.RunId, State.App, State.Trigger, State.Name, State.Status, State.Handled, State.ResultJson, State.Error);
}
