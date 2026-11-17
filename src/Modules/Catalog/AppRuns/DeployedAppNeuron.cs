namespace IntoChat.OS.Modules.Apps.AppRuns;

internal sealed record DeployedAppState(string App, string[] Commands, string[] Schedules)
{
    public DeployedAppState() : this("", [], []) { }
}

// One deployed sandbox app (key deployed/{app}). Each schedule cadence is one durable reminder; when it is due, the tick is an app run
// of trigger Schedule. A cadence that cannot be read is kept out, so a typo never ticks every minute. The brain's index hears of it.
[DigitalBrain.Neurons.Neuron("app.deployed")]
internal sealed class DeployedAppNeuron : DigitalBrain.Neurons.Neuron<DeployedAppState>, IDeployedApp
{
    private const string Prefix = "tick:";

    public async Task Set(string app, string[] commands, string[] schedules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        string[] readable = [.. (schedules ?? []).Where(cadence => Cadence.Parse(cadence) is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        await Stop(keep: readable);
        foreach (var cadence in readable)
        {
            var period = Cadence.Parse(cadence)!.Period;
            await Context.Reminders.Start(Prefix + cadence, period, period);
        }
        var next = new DeployedAppState(app, [.. (commands ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], readable);
        await Store(next);
        await Index().Put(new DeployedApp(next.App, next.Commands, next.Schedules));
    }

    public async Task Clear()
    {
        await Stop(keep: []);
        if (State.App.Length > 0) { await Index().Remove(State.App); }
        await Store(new DeployedAppState());
    }

    public Task<DeployedApp?> Read() => Task.FromResult(State.App.Length > 0 ? new DeployedApp(State.App, State.Commands, State.Schedules) : null);

    private IDeployedApps Index() => Context.Get<IDeployedApps>(IDeployedApps.Key(Id.Brain.Value));

    protected override async Task OnReminder(string name, CancellationToken cancellationToken)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) { await base.OnReminder(name, cancellationToken); return; }
        var cadence = name[Prefix.Length..];
        if (State.App.Length == 0 || !State.Schedules.Contains(cadence, StringComparer.Ordinal)) { await Context.Reminders.Stop(name); return; }
        var now = Context.Time.GetUtcNow();
        if (Cadence.Parse(cadence) is not { } parsed || !parsed.IsDue(now)) { return; }
        // The run key names the tick's minute, so a repeated delivery of the same tick starts nothing new.
        var runId = $"ticks/{State.App}/{now:yyyyMMddHHmm}-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cadence)))[..8]}";
        await Context.Get<IAppRun>(new DigitalBrain.NeuronId(Id.Brain, "app.run", runId)).Start(AppTrigger.Schedule, State.App, cadence, null);
    }

    private async Task Stop(string[] keep)
    {
        foreach (var reminder in await Context.Reminders.Names())
        {
            if (reminder.StartsWith(Prefix, StringComparison.Ordinal) && !keep.Contains(reminder[Prefix.Length..], StringComparer.Ordinal)) { await Context.Reminders.Stop(reminder); }
        }
    }
}
