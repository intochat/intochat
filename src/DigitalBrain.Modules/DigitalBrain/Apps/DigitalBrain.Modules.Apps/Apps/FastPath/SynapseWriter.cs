using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Models.OpenRouter;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Apps;

// The fast path: one sentence becomes one synapse of an app. IApp.WriteSynapse records the request and
// enqueues this; it runs on the work queue, never inside a grain turn, and reaches the app as an outside
// caller. One model call writes the synapse against the focus contracts, the app's shared classes and the
// best few synapses the caller can see (a close one is the base to fork, a proven one first); the shape and
// the compiler gate it, with at most one repair; WriteBehavior makes it live and CompleteSynapse records
// the outcome. The app neuron holds everything durable; the job completes its request on every path.
internal sealed class SynapseWriter(
    IGrainFactory grains,
    IKernel kernel,
    SynapseExamples examples,
    NeuronTypeSearch search,
    AppWorkQueue queue,
    IOptions<AppsOptions> options,
    TimeProvider clock,
    ILogger<SynapseWriter> log,
    IEnumerable<IAppRuntime> runtimes,
    IScriptSandbox? sandbox = null)
{
    internal static readonly ActivitySource Telemetry = new("DigitalBrain.Apps.FastPath");

    private const int MaxAttempts = 2;
    private const int MaxErrors = 20;
    private const int MaxSpecLength = 3000;
    private const int MaxSharedLength = 12000;
    // Every contract section beyond the focus neurons' own lines holds at most this many lines and characters.
    private const int MaxContractLines = 60;
    private const int MaxContractLength = 12000;
    // A focus neuron's own line is never dropped, only clipped past this.
    private const int MaxContractLineLength = 4000;
    private const int ContractMatches = 5;
    internal const string NoContract = "Refused, no contract: ";
    // A deploy call that times out may still land: the runtime joins a repeat of the same program to the one in
    // flight (IAppRuntime), so the writer asks again this many times before it gives the deploy up.
    internal const int MaxDeployCalls = 4;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _deploys = new(StringComparer.Ordinal);

    private string Model => options.Value.FastPathModel ?? nameof(IDeepSeekV41Flash);

    public void Enqueue(string appKey, Guid operationId, string sentence, string[] focus)
    {
        var job = new Job(appKey, operationId, sentence, focus, Stopwatch.GetTimestamp(), clock.GetUtcNow());
        queue.Enqueue($"synapse {appKey}", (_, cancellationToken) => Write(job, cancellationToken));
    }

    private sealed class DeployTurn
    {
        private SemaphoreSlim? _held;

        // A deploy was asked for and never answered: the runtime may hold a program the app's files do not.
        public bool Unsettled { get; set; }

        public long DeployMs { get; set; }


        public async Task Enter(SemaphoreSlim gate, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            _held = gate;
        }

        public void Exit() => Interlocked.Exchange(ref _held, null)?.Release();
    }

    private sealed record Job(string AppKey, Guid OperationId, string Sentence, string[] Focus, long Started, DateTimeOffset RequestedAt);

    private sealed record Outcome(string[] Errors, int Attempts, string? Base, string? Path, string? Hash = null, Dictionary<string, string>? Deployed = null);

    private sealed record WrittenSynapse(string? Path, string? Source);

    private async Task Write(Job job, CancellationToken cancellationToken)
    {
        // The span starts at the request, so its duration is the sentence-to-live time.
        using var span = Telemetry.StartActivity("apps.fastpath.write", ActivityKind.Internal, parentContext: default, startTime: job.RequestedAt);
        span?.SetTag("apps.app", job.AppKey);
        span?.SetTag("apps.fastpath.operation", job.OperationId.ToString());
        var app = grains.GetGrain<IApp>(job.AppKey);
        var deploys = grains.GetGrain<IAppDeploy>(job.AppKey);
        // The job owns the request's completion: every path below, a throw or a cancellation included, ends in
        // CompleteSynapse, so no request stays Requested while its job is gone.
        Outcome outcome;
        var turn = new DeployTurn();
        AppSynapse? recorded;
        long elapsed;
        try
        {
            try
            {
                var snapshot = await app.Read();
                // A request that is no longer waiting was answered already (a replay, or a job that ran twice): no model call.
                if (snapshot.Synapses?.FirstOrDefault(synapse => synapse.OperationId == job.OperationId) is not { Status: SynapseStatus.Requested })
                {
                    span?.SetTag("apps.fastpath.outcome", "skipped");
                    log.LogInformation("SynapseOutcome {App} {OperationId} {Outcome} {Reason}", job.AppKey, job.OperationId, "skipped", "the request was already answered");
                    return;
                }
                // The person's view goes alongside the write, never ahead of it; it lands before the outcome does.
                var working = Report(app, "working", "Writing a synapse: " + Clip(job.Sentence, 200));
                outcome = await Run(job, deploys, snapshot, turn, span, cancellationToken);
                await working;
            }
            catch (OperationCanceledException)
            { outcome = new(["The write was cancelled before it finished."], 0, null, null); }
            catch (Exception error)
            { outcome = new([$"The write failed: {error.GetType().Name}: {error.Message}"], 0, null, null); }
            elapsed = (long)Stopwatch.GetElapsedTime(job.Started).TotalMilliseconds;
            span?.SetTag("apps.fastpath.attempts", outcome.Attempts);
            span?.SetTag("apps.fastpath.base", outcome.Base ?? "");
            span?.SetTag("apps.fastpath.elapsed_ms", elapsed);
            // Runtime first, then the record: a deploy that never answered may still land, so the runtime is put back
            // to the app's files (which never held the staged synapse) before the rejection is recorded. When that
            // goes unanswered too, the runtime's state is unknown and the request says so: Unsettled, source kept.
            var unsettled = outcome.Errors.Length > 0 && turn.Unsettled && !await Reconcile(job.AppKey, deploys, cancellationToken);
            try
            {
                var completed = unsettled
                    ? await deploys.Unsettle(job.OperationId, "Unsettled: runtime state unknown. " + outcome.Errors[0], elapsed, outcome.Attempts, outcome.Base)
                    : await app.CompleteSynapse(job.OperationId, outcome.Errors, elapsed, outcome.Attempts, outcome.Base, outcome.Hash);
                recorded = completed.Synapses?.FirstOrDefault(synapse => synapse.OperationId == job.OperationId);
                // An edit through another path while this deploy ran (the Creator, settings) is in the app's files but
                // not in the program this write deployed: the runtime catches up with the files as they are now.
                if (recorded is { Status: SynapseStatus.Written } && outcome.Deployed is { } deployed
                    && !Same(deployed, completed.Files.Where(file => AppFiles.IsProgram(file.Key)).ToDictionary(StringComparer.Ordinal)))
                { await Reconcile(job.AppKey, deploys, cancellationToken); }
            }
            catch (Exception error)
            {
                // The completion message may still land after its caller gave up; nothing else can record it.
                span?.SetTag("apps.fastpath.outcome", "unrecorded");
                span?.SetStatus(ActivityStatusCode.Error, Clip(error.Message, 500));
                log.LogError(error, "SynapseOutcome {App} {OperationId} {Outcome} {Reason}", job.AppKey, job.OperationId, "unrecorded",
                    outcome.Errors.Length == 0 ? "written, but the completion could not be recorded" : Clip(outcome.Errors[0], 500));
                return;
            }
        }
        // The app's deploy turn ends once the outcome is recorded: the next write's deploy queues behind this
        // completion, never ahead of it, so the completion is not left waiting out another Assign.
        finally { turn.Exit(); }
        if (recorded is { Status: SynapseStatus.Unsettled })
        {
            span?.SetTag("apps.fastpath.outcome", "unsettled");
            span?.SetStatus(ActivityStatusCode.Error, Clip(recorded.Errors[0], 500));
            log.LogError("SynapseOutcome {App} {OperationId} {Outcome} {Reason} {ElapsedMs} {DeployMs} {Attempts}",
                job.AppKey, job.OperationId, "unsettled", recorded.Errors[0], elapsed, turn.DeployMs, outcome.Attempts);
            await Report(app, "attention", "The synapse's deploy never answered; what runs is unknown: " + Clip(recorded.Errors[0], 300));
        }
        else if (recorded is { Status: SynapseStatus.Written })
        {
            span?.SetTag("apps.fastpath.outcome", "written");
            span?.SetTag("apps.fastpath.path", recorded.Path);
            log.LogInformation("SynapseOutcome {App} {OperationId} {Outcome} {Reason} {Path} {ElapsedMs} {DeployMs} {Attempts} {Base}",
                job.AppKey, job.OperationId, "written", "", recorded.Path, elapsed, turn.DeployMs, outcome.Attempts, outcome.Base);
            await Report(app, "completed", $"Wrote the synapse {recorded.Path}");
        }
        else
        {
            string[] errors = recorded is { Errors.Length: > 0 } ? recorded.Errors : outcome.Errors;
            var reason = errors.Length > 0 ? string.Join(" | ", errors) : "unknown";
            span?.SetTag("apps.fastpath.outcome", "rejected");
            span?.SetStatus(ActivityStatusCode.Error, Clip(reason, 500));
            log.LogInformation("SynapseOutcome {App} {OperationId} {Outcome} {Reason} {ElapsedMs} {DeployMs} {Attempts}",
                job.AppKey, job.OperationId, "rejected", reason, elapsed, turn.DeployMs, outcome.Attempts);
            await Report(app, "attention", "Could not write the synapse: " + Clip(errors.FirstOrDefault() ?? reason, 300));
        }
    }

    private async Task<Outcome> Run(Job job, IAppDeploy deploys, AppSnapshot snapshot, DeployTurn turn, Activity? span, CancellationToken cancellationToken)
    {
        var brain = BrainOf(job.AppKey);
        // The sentence is embedded once for both rankings, and the examples, the focus and the matching contracts
        // are gathered together.
        var query = examples.Embed(job.Sentence, cancellationToken);
        var types = kernel.Types(cancellationToken);
        // Fork-first: a close enough synapse is the base the model edits.
        var ranking = examples.Rank(brain, job.Sentence, cancellationToken, query);
        var focus = FocusOf(job, brain, types, cancellationToken);
        var matches = MatchesOf(job.Sentence, types, query, cancellationToken);
        await Task.WhenAll(ranking, focus, matches);
        var (ranked, basis) = await ranking;
        span?.SetTag("apps.fastpath.examples", string.Join(",", ranked.Select(example => example.Name)));
        var prompt = Prompt(job, snapshot, ranked, basis, await focus, await matches);
        var system = AgentPrompts.Synapse + (sandbox is null ? "" : "\n\n" + sandbox.AuthoringDescription);
        WrittenSynapse? written = null;
        string[] errors = [];
        var attempts = 0;
        while (attempts < MaxAttempts)
        {
            var request = attempts == 0 ? prompt : Repair(prompt, written, errors);
            attempts++;
            string reply;
            try { reply = await Generate(brain, system, request, span); }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new([$"The model {Model} did not answer: {error.Message}"], attempts, basis?.Name, null); }
            // A refusal is the model saying no installed contract carries the sentence: recorded, never compiled or deployed.
            if (Refusal(reply) is { } missing)
            {
                span?.SetTag("apps.fastpath.refused", missing);
                return new([NoContract + missing], attempts, basis?.Name, null);
            }
            (written, errors) = await Gate(reply, snapshot, cancellationToken);
            if (errors.Length == 0) { break; }
        }
        if (errors.Length > 0) { return new(errors, attempts, basis?.Name, null); }
        // One deploy per app at a time, held until the outcome is recorded (Write releases it). The deploy runs
        // here, outside the app's turn: the source is staged on its request (Stage), then the runtime, however long it takes.
        await turn.Enter(_deploys.GetOrAdd(job.AppKey, _ => new SemaphoreSlim(1, 1)), cancellationToken);
        StagedProgram program;
        try { program = await deploys.Stage(job.OperationId, written!.Path!, written.Source!); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { return new([error.Message], attempts, basis?.Name, null); }
        var deployed = await Deploy(job.AppKey, program, turn, cancellationToken);
        // The runtime compiles the whole program again as it assigns it: that refusal is this synapse's too.
        if (deployed.Error is { } refused) { return new([refused], attempts, basis?.Name, null); }
        return new([], attempts, basis?.Name, written.Path, deployed.Hashes.GetValueOrDefault(written.Path!), program.Sources);
    }

    private IAppRuntime? Runtime => runtimes.FirstOrDefault();

    // The whole program to the runtime, asked again while a call times out (a repeat joins the deploy in flight).
    // Unsettled stays set when no call answered: the program may land after all, and Reconcile undoes it.
    private async Task<(IReadOnlyDictionary<string, string> Hashes, string? Error)> Deploy(string appKey, StagedProgram program, DeployTurn turn, CancellationToken cancellationToken)
    {
        if (Runtime is not { } runtime) { return (new Dictionary<string, string>(), null); }
        var started = Stopwatch.GetTimestamp();
        turn.Unsettled = true;
        // The program runs as the app's principal, as it did when the app neuron deployed it, never as whoever
        // this background job happened to inherit.
        var inherited = RequestContext.Get(CallerContextStamper.RequestContextKey);
        if (program.Principal is { } principal) { CallerContextStamper.Stamp(principal); }
        else { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
        try
        {
            for (var call = 1; ; call++)
            {
                try
                {
                    var hashes = program.Sources.Count == 0
                        ? new Dictionary<string, string>()
                        : await runtime.Deploy(appKey, program.Sources, program.Configuration, cancellationToken);
                    if (program.Sources.Count == 0) { await runtime.Retire(appKey, cancellationToken); }
                    turn.Unsettled = false;
                    return (hashes, null);
                }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException)
                {
                    turn.Unsettled = false;
                    return (new Dictionary<string, string>(), error.Message);
                }
                catch (TimeoutException error) when (call < MaxDeployCalls)
                { log.LogInformation("SynapseDeployWaiting {App} {Call} {Reason}", appKey, call, Clip(error.Message, 300)); }
                catch (TimeoutException error)
                { return (new Dictionary<string, string>(), $"The deploy did not answer after {MaxDeployCalls} calls: {error.Message}"); }
            }
        }
        finally
        {
            turn.DeployMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (inherited is null) { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { RequestContext.Set(CallerContextStamper.RequestContextKey, inherited); }
        }
    }

    private static bool Same(Dictionary<string, string> left, Dictionary<string, string> right)
        => left.Count == right.Count && left.All(file => right.TryGetValue(file.Key, out var source) && source == file.Value);

    // The runtime to the app's files as they are now (a staged synapse is never among them): after a deploy that
    // never answered and may still land, the later program replaces it, and the brain runtime refuses the older
    // Assign by ticket. False when this goes unanswered too.
    private async Task<bool> Reconcile(string appKey, IAppDeploy deploys, CancellationToken cancellationToken)
    {
        var settle = new DeployTurn();
        try
        {
            var reconciled = await Deploy(appKey, await deploys.Program(), settle, cancellationToken);
            if (reconciled.Error is null && !settle.Unsettled) { return true; }
            log.LogError("SynapseUnreconciled {App} {Reason}", appKey, reconciled.Error ?? "the runtime did not answer");
            return false;
        }
        catch (Exception error)
        {
            log.LogError(error, "SynapseUnreconciled {App} {Reason}", appKey, error.Message);
            return false;
        }
    }

    // One capped model call: the fast path's reasoning effort and output cap ride on the selection, so the
    // typed model neuron keeps its own model and only the thinking budget changes.
    private async Task<string> Generate(string brain, string system, string prompt, Activity? span)
    {
        var settings = options.Value;
        var selection = new ModelSelection(Reasoning: settings.FastPathReasoning is { Length: > 0 } reasoning ? reasoning.ToLowerInvariant() : null,
            MaxOutputTokens: settings.FastPathMaxOutputTokens);
        var result = await grains.GetGrain<ICreatorModel>(0).Generate(brain, Model, new InferenceRequest(
            [new AiMessage("system", [new AiText(system)]), new AiMessage("user", [new AiText(prompt)])], selection));
        span?.SetTag("apps.fastpath.input_tokens", result.Usage?.InputTokens);
        span?.SetTag("apps.fastpath.output_tokens", result.Usage?.OutputTokens);
        var text = string.Concat(result.Messages.SelectMany(message => message.Content).OfType<AiText>().Select(content => content.Text)).Trim();
        // A reply cut at the cap is half a file: say so instead of handing the shape a broken JSON object.
        if (string.Equals(result.FinishReason, "length", StringComparison.OrdinalIgnoreCase) && Refusal(text) is null)
        { throw new InvalidDataException($"The reply hit the {selection.MaxOutputTokens}-token output cap."); }
        return text;
    }

    // The reply's one file, held to the shape first and then to the compiler with the app's shared classes.
    private async Task<(WrittenSynapse? Written, string[] Errors)> Gate(string reply, AppSnapshot snapshot, CancellationToken cancellationToken)
    {
        WrittenSynapse written;
        try { written = Creator.Parse<WrittenSynapse>(reply); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { return (null, [$"The reply was not the JSON object {{\"path\": \"name.cs\", \"source\": \"...\"}}: {error.Message}"]); }
        if (PathError(written.Path) is { } wrongPath) { return (written, [wrongPath]); }
        if (string.IsNullOrWhiteSpace(written.Source)) { return (written, ["The reply's source is empty."]); }
        if (SynapseShape.Check(written.Path!, written.Source) is { Length: > 0 } shape) { return (written, Format(shape)); }
        if (sandbox is null) { return (written, [AppAuthoringPolicy.SandboxMissing]); }
        var files = Shared(snapshot).ToDictionary(StringComparer.Ordinal);
        files[written.Path!] = written.Source;
        try
        {
            var check = await sandbox.Compile(files, cancellationToken);
            return check.Success ? (written, []) : (written, check.Errors.Count > 0 ? Format(check.Errors) : ["The synapse does not compile."]);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { return (written, [error.Message]); }
    }

    // The reply's first line when it is the prompt's refusal marker, with what it says is missing.
    internal static string? Refusal(string reply)
    {
        var line = reply.Replace("```", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line is null || !line.StartsWith(AgentPrompts.Refusal, StringComparison.OrdinalIgnoreCase)) { return null; }
        var missing = line[AgentPrompts.Refusal.Length..].Trim();
        return Clip(missing.Length > 0 ? missing : "the model did not say what is missing", 500);
    }

    private static string? PathError(string? path) => path switch
    {
        null or "" => "The reply names no path.",
        AppFiles.DeclarationPath => "app.cs is the app's declaration, never a synapse: name a new .cs file.",
        _ when !AppFiles.IsProgram(path) => "A synapse is a .cs file.",
        _ when path.Contains("..", StringComparison.Ordinal) || path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal)
            => "A synapse path is a file name inside the app.",
        _ => null,
    };

    private static string[] Format(IEnumerable<ScriptCompilationDiagnostic> errors)
        => [.. errors.Take(MaxErrors).Select(error => $"{error.File}:{error.Line} {error.Id} {error.Message}")];

    private static string Repair(string prompt, WrittenSynapse? written, string[] errors)
    {
        var text = new StringBuilder(prompt).AppendLine().AppendLine();
        if (written is { Source.Length: > 0 }) { text.AppendLine("Your previous reply:").AppendLine($"--- {written.Path} ---").AppendLine(written.Source).AppendLine(); }
        text.AppendLine("It was refused:");
        foreach (var error in errors) { text.AppendLine("- " + error); }
        return text.AppendLine().AppendLine("Fix every error and reply with the corrected JSON object only.").ToString();
    }

    private async Task<(string[] Focused, string[] Contracts)> FocusOf(Job job, string brain, Task<TypeEntry[]> types, CancellationToken cancellationToken)
        => await Focus(job, brain, await types, cancellationToken);

    private async Task<string[]> MatchesOf(string sentence, Task<TypeEntry[]> types, Task<float[]?> query, CancellationToken cancellationToken)
        => await Matches(sentence, await types, await query, cancellationToken);

    private string Prompt(Job job, AppSnapshot app, SynapseExample[] ranked, SynapseExample? basis,
        (string[] Focused, string[] Contracts) focus, string[] matching)
    {
        var manifest = app.Manifest;
        var text = new StringBuilder();
        text.AppendLine($"The app: {manifest.Title} ({job.AppKey}). {manifest.Description}");
        if (manifest.Operations.Length > 0)
        {
            text.AppendLine("Its operations (each arrives as AppRequested):");
            foreach (var operation in manifest.Operations) { text.AppendLine($"- {operation.Name}: {operation.Description}"); }
        }
        if (manifest.Settings.Length > 0)
        {
            text.AppendLine("Its settings (brain.Setting(name)):");
            foreach (var setting in manifest.Settings) { text.AppendLine($"- {setting.Name}: {setting.Description}"); }
        }
        var programs = app.Files.Keys.Where(AppFiles.IsProgram).Order(StringComparer.Ordinal).ToArray();
        text.AppendLine(programs.Length == 0 ? "It has no program files yet."
            : $"Its program files: {string.Join(", ", programs)}. Name a new file unless the sentence asks to change one of these.");
        if (app.Spec.Trim().Length > 0) { text.AppendLine().AppendLine("Its specification:").AppendLine(Clip(app.Spec.Trim(), MaxSpecLength)); }

        var shared = Shared(app).ToArray();
        if (shared.Length > 0)
        {
            text.AppendLine().AppendLine("The app's shared classes, compiled into every synapse (use them, do not repeat them):");
            var budget = MaxSharedLength;
            foreach (var (path, source) in shared)
            {
                if (budget <= 0) { break; }
                text.AppendLine($"--- {path} ---").AppendLine(Clip(source, budget));
                budget -= source.Length;
            }
        }

        var (focused, focusContracts) = focus;
        if (focused.Length > 0)
        {
            text.AppendLine().AppendLine("What the person is looking at (neuron keys in focus):");
            foreach (var line in focused) { text.AppendLine("- " + line); }
        }
        if (focusContracts.Length > 0)
        {
            text.AppendLine().AppendLine("Contracts of the neurons in focus:");
            foreach (var line in focusContracts) { text.AppendLine("- " + line); }
        }
        string[] matches = [.. Bounded(matching.Except(focusContracts, StringComparer.Ordinal))];
        if (matches.Length > 0)
        {
            text.AppendLine().AppendLine("Contracts that match the sentence:");
            foreach (var line in matches) { text.AppendLine("- " + line); }
        }

        var shown = ranked.Where(example => example != basis).ToArray();
        if (shown.Length > 0)
        {
            text.AppendLine().AppendLine("Synapses this brain can see, as examples of the shape and the usings:");
            foreach (var example in shown) { AppendExample(text, example); }
        }
        if (basis is not null)
        {
            text.AppendLine().AppendLine("The base synapse to fork: edit it into the requested synapse.");
            AppendExample(text, basis);
        }
        return text.AppendLine().AppendLine("The person said:").AppendLine(job.Sentence).ToString();
    }

    private static void AppendExample(StringBuilder text, SynapseExample example)
        => text.AppendLine($"--- {example.Name} ---").AppendLine("// " + example.Spec.Replace("\n", "\n// ", StringComparison.Ordinal)).AppendLine(example.Source);

    // Ino's tether: each focus key's neuron type (from the kernel's instances) and the contracts to write against.
    // The focus neurons' own contracts lead and are never cut; their modules' signals, then the rest of their
    // modules' contracts as the sandbox reads them, follow inside a bounded budget however large the modules are.
    private async Task<(string[] Focused, string[] Contracts)> Focus(Job job, string brain, TypeEntry[] types, CancellationToken cancellationToken)
    {
        if (job.Focus.Length == 0) { return ([], []); }
        var wanted = job.Focus.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var instance in kernel.InstancesIn(brain, null, cancellationToken))
        {
            if (wanted.Contains(instance.Key)) { found[instance.Key] = instance.Type; }
            if (found.Count == wanted.Count) { break; }
        }
        var focused = job.Focus.Select(key => (Key: key, Entry: found.TryGetValue(key, out var type)
            ? types.FirstOrDefault(entry => entry.Kind == TypeKind.Neuron && entry.Alias == type) : null)).ToArray();
        TypeEntry[] entries = [.. focused.Select(item => item.Entry).OfType<TypeEntry>().DistinctBy(entry => entry.Name)];
        string[] keys = [.. focused.Select(item => item.Entry is { } entry ? $"{item.Key} ({entry.Name})" : $"{item.Key} (no neuron with state under this key yet)")];
        if (entries.Length == 0) { return (keys, []); }
        var rest = Signals(entries, types).Concat(await ModuleContracts(entries, cancellationToken))
            // A focus neuron's line from the sandbox is the one already leading, whatever its text.
            .Where(line => !entries.Any(entry => line.StartsWith(entry.Name + " [", StringComparison.Ordinal)));
        return (keys, [.. Neurons(entries).Select(line => Clip(line, MaxContractLineLength)), .. Bounded(rest)]);
    }

    // The focus modules' contracts as the sandbox reads them, cut short or not; a reading that fails leaves them out.
    private async Task<IReadOnlyList<string>> ModuleContracts(TypeEntry[] entries, CancellationToken cancellationToken)
    {
        if (sandbox is null) { return []; }
        try { return (await sandbox.ReadContracts([.. entries.Select(entry => entry.Provider).Distinct(StringComparer.OrdinalIgnoreCase)], cancellationToken)).Contracts; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            log.LogDebug(error, "The fast path's contract reading failed; the prompt goes with the focus contracts alone");
            return [];
        }
    }

    // Contract lines in order while the line count and the text budget last; a line too long for what is left is skipped.
    private static IEnumerable<string> Bounded(IEnumerable<string> lines)
    {
        var count = MaxContractLines;
        var length = MaxContractLength;
        foreach (var line in lines.Distinct(StringComparer.Ordinal))
        {
            if (count == 0) { yield break; }
            if (line.Length > length) { continue; }
            count--;
            length -= line.Length;
            yield return line;
        }
    }

    // The kernel's neuron contracts closest to the sentence (the Builder's search_contracts), with their modules'
    // signals, ranked by the sentence's one embedding when there is one. A hint beside the focus and the examples:
    // a search that fails leaves it out. The prompt drops the lines the focus already shows and bounds the rest.
    private async Task<string[]> Matches(string sentence, TypeEntry[] types, float[]? query, CancellationToken cancellationToken)
    {
        var neurons = types.Where(entry => entry.Kind == TypeKind.Neuron).ToArray();
        if (neurons.Length == 0) { return []; }
        IReadOnlyList<NeuronTypeHit> hits;
        try { hits = await search.Search(sentence, [.. neurons.Select(NeuronType.From)], ContractMatches, cancellationToken, query); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "The fast path's contract search failed; the prompt goes without matching contracts");
            return [];
        }
        TypeEntry[] entries = [.. hits.Select(hit => neurons.FirstOrDefault(entry => entry.Name == hit.Type.Contract)).OfType<TypeEntry>()];
        return [.. Neurons(entries).Concat(Signals(entries, types)).Distinct(StringComparer.Ordinal)];
    }

    // The same line format as the sandbox's contract reading: each neuron, then its module's signals.
    private static IEnumerable<string> Neurons(TypeEntry[] neurons)
        => neurons.Select(entry => $"{entry.Name} [neuron id: {entry.Alias}/<key>] — {entry.Description} {{ {string.Join("; ", entry.Shape)} }}");

    private static IEnumerable<string> Signals(TypeEntry[] neurons, TypeEntry[] types)
        => types.Where(type => type.Kind == TypeKind.Signal && neurons.Any(entry => string.Equals(entry.Provider, type.Provider, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(signal => signal.Name, StringComparer.Ordinal)
            .Select(signal => signal.Name + " { " + string.Join("; ", signal.Shape) + " }");

    private static IEnumerable<KeyValuePair<string, string>> Shared(AppSnapshot app)
        => app.Files.Where(file => AppFiles.IsProgram(file.Key) && SynapseShape.IsShared(file.Value)).OrderBy(file => file.Key, StringComparer.Ordinal);

    private async Task Report(IApp app, string state, string detail)
    {
        // Activity is the person's view of the write; it must never decide the outcome.
        try { await app.Progress(state, detail); }
        catch (Exception error) when (error is not OperationCanceledException)
        { log.LogDebug(error, "Fast-path activity could not be reported"); }
    }

    private static string BrainOf(string appKey)
    {
        var marker = appKey.LastIndexOf("/apps/", StringComparison.Ordinal);
        return marker > 0 ? appKey[..marker] : BrainKey.BrainOf(appKey);
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..Math.Max(0, length)] + "…";
}
