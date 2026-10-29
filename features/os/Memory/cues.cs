using System.Reflection;
using IntoChat.OS;
using IntoChat.OS.Modules.AI.Agents.Intents;
using IntoChat.OS.Modules.AI.Agents.Intents.Signals;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Time.Reminders;
using IntoChat.OS.Modules.Time.Reminders.Signals;

// Cues move a Waiting intent forward. Every Waiting intent with cues is watched: a signal cue follows the
// publisher named by its key (the neuron's contract is found through the kernel, the signal type from its
// alias among the loaded contracts), a time cue starts an IReminder at {app}/cues/{intent id} and stops it on
// its first tick. The first cue to fire moves the intent to Dispatched ("cue: <alias or time>"), and only
// while it is still Waiting. An intent that stops waiting drops its watchers. A cue that cannot be watched is
// written to {app}/status and the intent stays Waiting; its watchers are dropped so the next sync tries again.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var status = brain.Get<IText>(appKey + "/status");
var intents = brain.Get<IIntents>(IIntents.Key(Memory.Scope(brain)));
var watched = new Dictionary<Guid, CancellationTokenSource>();
var gate = new SemaphoreSlim(1, 1);
var watchers = new HashSet<Task>();
// A cue names a [Signal] record by its alias; a publisher's contract is the interface whose default neuron type is the type the
// kernel lists the publisher under.
var signalTypes = new Intents.CueTypes(() => Load(type => !type.IsAbstract && typeof(DigitalBrain.Signal).IsAssignableFrom(type),
    type => type.GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false)?.Alias), Assemblies);
var contractTypes = new Intents.CueTypes(() => Load(type => type.IsInterface,
    type => type.GetCustomAttribute<DigitalBrain.Contracts.DefaultNeuronTypeAttribute>(inherit: false)?.Type), Assemblies);

var following = Supervise("intent transitions", ct => brain.React<IntentTransitioned>(intents, token => SyncAll(token), ct));

await foreach (var armed in brain.On<IntentArmed>(intents, () => SyncAll(brain.Stopping), brain.Stopping))
{
    await Sync(armed.IntentId, brain.Stopping);
}
await following;
Task[] pending;
lock (watchers) { pending = [.. watchers]; }
await Task.WhenAll(pending);

// Start the watchers of every Waiting intent that has none and drop those of intents that stopped waiting.
async Task SyncAll(CancellationToken ct)
{
    try
    {
        var snapshot = await intents.Read();
        await gate.WaitAsync(ct);
        try
        {
            foreach (var intent in snapshot.Intents) { Apply(intent, false); }
            var live = snapshot.Intents.Where(intent => intent.State == IntentState.Waiting).Select(intent => intent.Id).ToHashSet();
            foreach (var id in watched.Keys.Where(id => !live.Contains(id)).ToArray()) { Drop(id); }
        }
        finally { gate.Release(); }
    }
    catch (Exception error) when (!ct.IsCancellationRequested && !brain.Stopping.IsCancellationRequested)
    { await Report("Memory could not read the intents for their cues: " + error.Message); }
}

// An armed intent restarts its watchers: its cues may have changed.
async Task Sync(Guid id, CancellationToken ct)
{
    try
    {
        var snapshot = await intents.Read();
        var intent = snapshot.Intents.FirstOrDefault(candidate => candidate.Id == id);
        await gate.WaitAsync(ct);
        try
        {
            if (intent is null) { Drop(id); } else { Apply(intent, true); }
        }
        finally { gate.Release(); }
    }
    catch (Exception error) when (!ct.IsCancellationRequested && !brain.Stopping.IsCancellationRequested)
    { await Report("Memory could not arm the cues of intent " + id + ": " + error.Message); }
}

// Called under the gate.
void Apply(Intent intent, bool restart)
{
    var tracked = watched.ContainsKey(intent.Id);
    if (intent.State != IntentState.Waiting || intent.Cues.Length == 0) { if (tracked) { Drop(intent.Id); } return; }
    if (tracked && !restart) { return; }
    if (tracked) { Drop(intent.Id); }
    var source = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    watched[intent.Id] = source;
    foreach (var cue in intent.Cues)
    {
        var task = Watch(intent.Id, cue, source.Token);
        lock (watchers) { watchers.Add(task); }
        // A finished watcher is not kept.
        _ = task.ContinueWith(done => { lock (watchers) { watchers.Remove(done); } }, TaskScheduler.Default);
    }
}

void Drop(Guid id)
{
    if (!watched.Remove(id, out var source)) { return; }
    source.Cancel();
    source.Dispose();
}

async Task Watch(Guid id, Cue cue, CancellationToken ct)
{
    try
    {
        if (cue.Kind == "time") { await WatchTime(id, cue, ct); }
        else { await WatchSignal(id, cue, ct); }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested || brain.Stopping.IsCancellationRequested) { }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await Report("Memory could not watch a cue of intent " + id + ": " + error.Message + "; the intent stays Waiting.");
        await Forget(id, ct);
    }
}

// A watcher that failed drops its intent's entry (only while it is still the one it was started under), so the
// next SyncAll sees the Waiting intent untracked and starts its watchers again.
async Task Forget(Guid id, CancellationToken ct)
{
    try
    {
        await gate.WaitAsync(brain.Stopping);
        try { if (watched.TryGetValue(id, out var source) && source.Token == ct) { Drop(id); } }
        finally { gate.Release(); }
    }
    catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { }
}

async Task WatchTime(Guid id, Cue cue, CancellationToken ct)
{
    if (cue.At is not { } at) { throw new InvalidOperationException("a time cue has no time"); }
    var reminder = brain.Get<IReminder>(Intents.ReminderKey(appKey, id));
    var due = at - DateTimeOffset.UtcNow;
    if (due < TimeSpan.FromSeconds(1)) { due = TimeSpan.FromSeconds(1); }
    try
    {
        await reminder.Start(due, TimeSpan.FromDays(365));
        await foreach (var _ in brain.On<ReminderTick>(reminder, ct))
        {
            if (!await Fire(id, "time")) { await Forget(id, ct); }
            return;
        }
    }
    finally
    {
        // Whether it fired, failed or was dropped, the periodic reminder must not outlive the watcher.
        try { await reminder.Stop(); }
        catch (Exception) when (brain.Stopping.IsCancellationRequested) { }
    }
}

async Task WatchSignal(Guid id, Cue cue, CancellationToken ct)
{
    var type = signalTypes.Resolve(cue.SignalAlias) ?? throw new InvalidOperationException("no loaded signal has the alias " + cue.SignalAlias);
    var contract = await ContractOf(cue.PublisherKey, ct) ?? throw new InvalidOperationException("the key " + cue.PublisherKey + " is not a known neuron");
    var get = typeof(IntoChatOSConnection).GetMethods().First(method => method.Name == "Get" && method.IsGenericMethodDefinition
        && method.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(string));
    var publisher = get.MakeGenericMethod(contract).Invoke(brain, [cue.PublisherKey])!;
    var on = typeof(IntoChatOSConnection).GetMethods().First(method => method.Name == "On" && method.IsGenericMethodDefinition
        && method.GetParameters() is [{ ParameterType.Name: "Object" }, { ParameterType.Name: "CancellationToken" }]);
    var stream = (IAsyncEnumerable<DigitalBrain.Signal>)on.MakeGenericMethod(type).Invoke(brain, [publisher, ct])!;
    await foreach (var signal in stream.WithCancellation(ct))
    {
        if (!Intents.CueMatches(cue, signal)) { continue; }
        if (await Fire(id, cue.SignalAlias)) { return; }
    }
}

// True when the intent was moved or no longer waits; false when every attempt failed (reported on status).
async Task<bool> Fire(Guid id, string what)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var snapshot = await intents.Read();
            var intent = snapshot.Intents.FirstOrDefault(candidate => candidate.Id == id);
            if (!Intents.IsWaiting(intent)) { return true; }
            await intents.Transition(id, IntentState.Dispatched, "cue: " + what, snapshot.Revision);
            return true;
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            if (Intents.ShouldRetry(error, attempt)) { continue; }
            await Report("Memory could not dispatch intent " + id + " on its cue: " + error.Message + "; the intent stays Waiting.");
            return false;
        }
    }
}

static int Assemblies() => AppDomain.CurrentDomain.GetAssemblies().Length;

// Name to type of every loaded type the filter accepts; the first type with a name wins.
static IReadOnlyDictionary<string, Type> Load(Func<Type, bool> accept, Func<Type, string?> name)
{
    var map = new Dictionary<string, Type>();
    foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(TypesOf).Where(accept))
    {
        if (name(type) is { } key) { map.TryAdd(key, type); }
    }
    return map;
}

static IEnumerable<Type> TypesOf(Assembly assembly)
{
    try { return assembly.GetTypes(); }
    catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); }
}

// The neuron contract of an existing instance: the kernel names its neuron type, a loaded contract declares it as its default.
// An alias not yet loaded is looked up again by CueTypes, throttled per alias.
async Task<Type?> ContractOf(string key, CancellationToken ct)
{
    await foreach (var instance in brain.Kernel.Instances(null, ct))
    {
        if (instance.Key == key) { return contractTypes.Resolve(instance.Type); }
    }
    return null;
}

async Task Report(string text)
{
    try { await status.Set(text); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine("Memory could not write its status (" + error.Message + "): " + text); }
}

async Task Supervise(string name, Func<CancellationToken, Task> watch)
{
    while (!brain.Stopping.IsCancellationRequested)
    {
        string problem;
        try { await watch(brain.Stopping); problem = "ended"; }
        catch (OperationCanceledException) when (brain.Stopping.IsCancellationRequested) { return; }
        catch (Exception error) { problem = "failed: " + error.Message; }
        await Report("Memory's watcher of " + name + " " + problem + "; restarting.");
        try { await Task.Delay(TimeSpan.FromSeconds(5), brain.Stopping); }
        catch (OperationCanceledException) { return; }
    }
}
