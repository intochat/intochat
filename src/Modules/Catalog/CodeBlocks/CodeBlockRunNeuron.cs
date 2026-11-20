using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Confirmation;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.CodeBlocks;

internal sealed record CodeBlockRunState(
    string RunId,
    CodeBlockStatus Status,
    string[] Steps,
    string? Output,
    string? Error,
    string Title,
    string Source,
    string Input,
    string[] Needs,
    string? PlanId)
{
    public CodeBlockRunState() : this("", CodeBlockStatus.Running, [], null, null, "", "", "", [], null) { }
}

// One code block run (key = run id). It compiles the block, reads its needs from the compiled code, and when that need set is not yet
// approved asks a person once (and waits, across restarts). Then it binds the block's app key to its needs, deploys it to the sandbox
// container, runs it, unloads it, and keeps the calls the block made (from the edge, allowed or refused) as its steps.
[DigitalBrain.Neurons.Neuron("codeblock.run")]
internal sealed class CodeBlockRunNeuron(AppPackageCompiler compiler, ISandboxRuntimeBridge sandbox) : DigitalBrain.Neurons.Neuron<CodeBlockRunState>, ICodeBlockRun
{
    // The needs of the run in progress, kept with its outcome.
    private string[]? _needs;

    public async Task<CodeBlockRunView> Start(string title, string source, string input)
    {
        if (State.RunId.Length > 0) { return View(); }
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var name = string.IsNullOrWhiteSpace(title) ? "code block" : title;
        await Save(new CodeBlockRunState() with { RunId = Id.Key, Title = name, Source = source, Input = input ?? "" }, new CodeBlockRunStarted(Id.Key, name));

        var compiled = Compile();
        if (!compiled.Succeeded) { return await Finish(["compile"], null, Refusal(compiled)); }
        var needs = CodeBlockNeeds.Infer(compiled.AssemblyBytes);
        if (CodeBlockNeeds.Forbidden(needs) is { Length: > 0 } forbidden)
        {
            return await Finish(["compile"], null, $"A code block may not use {string.Join(", ", forbidden)}: those decide authority.");
        }
        if (needs.Length > 0 && !await Grants().IsApproved(CodeBlockNeeds.Hash(needs)))
        {
            var planId = $"cb-{Id.Key}";
            await Context.Wires.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain));
            await Save(State with { Status = CodeBlockStatus.Waiting, Steps = ["compile"], Needs = needs, PlanId = planId }, new CodeBlockRunWaiting(Id.Key, planId, needs));
            await Confirmations().Request(new ConfirmationPlan(planId, ConfirmationAction.Other, false, $"Let \"{name}\" use {needs.Length} contract(s)?",
                $"The code block \"{name}\" calls: {string.Join(", ", needs.Select(Short))}. Approving lets any code with these needs use them.",
                "Allow", "Deny", name)
            { RequiresPerson = true, Kind = "codeblock.needs", Subject = CodeBlockNeeds.Hash(needs) });
            return View();
        }
        return await Execute(compiled, needs);
    }

    public Task<CodeBlockRunView?> Read() => Task.FromResult(State.RunId.Length > 0 ? View() : null);

    public async Task<CodeBlockRunView?> Cancel(string reason)
    {
        if (State.RunId.Length == 0) { return null; }
        if (State.Status != CodeBlockStatus.Waiting) { return View(); }
        if (State.PlanId is { } planId)
        {
            // Only who raised a question may withdraw it, or a person; otherwise it is left to expire and its answer is ignored.
            try { await Confirmations().Withdraw(planId); }
            catch (UnauthorizedAccessException refused) { OsConsoleLog.For<CodeBlockRunNeuron>().LogSwallowed(refused, "The question stays until it expires; the cancelled run ignores its answer."); }
        }
        var why = string.IsNullOrWhiteSpace(reason) ? "Cancelled." : reason;
        await Save(State with { Status = CodeBlockStatus.Cancelled, Error = why, PlanId = null }, new CodeBlockRunDone(Id.Key, false, null, why, Cancelled: true));
        return View();
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (State.Status == CodeBlockStatus.Waiting && State.PlanId is { } planId)
        {
            if (await Confirmations().Outcome(planId) is { } outcome) { await Answer(outcome); }
            else { await Context.Wires.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain)); }
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task OnResolved(ConfirmationResolved resolved)
    {
        if (State.Status != CodeBlockStatus.Waiting || !string.Equals(resolved.PlanId, State.PlanId, StringComparison.Ordinal)) { return; }
        if (await Confirmations().Outcome(resolved.PlanId) is { } outcome) { await Answer(outcome); }
    }

    private async Task Answer(ConfirmationResult outcome)
    {
        if (outcome.Outcome != ConfirmationOutcome.Approved)
        {
            await Finish(["compile"], null, "The person did not allow this code block's needs.");
            return;
        }
        await Grants().Approve(CodeBlockNeeds.Hash(State.Needs), State.Needs);
        var compiled = Compile();
        await (compiled.Succeeded ? Execute(compiled, State.Needs) : Finish(["compile"], null, Refusal(compiled)));
    }

    private async Task<CodeBlockRunView> Execute(CompiledAppPackage compiled, string[] needs)
    {
        _needs = needs;
        var steps = new List<string> { "compile" };
        var appKey = CodeBlockSource.AppKey(Id.Key);
        var grants = Grants();
        await grants.Bind(appKey, Id.Key, needs);
        try
        {
            await sandbox.DeployPackageAsync(Id.Brain.Value, compiled.EnrichedPackage!, compiled.AssemblyBytes, null, null);
            steps.Add("deploy");
            SandboxDispatchResult result;
            try
            {
                result = await sandbox.DispatchCommandAsync(Id.Brain.Value, appKey, CodeBlockSource.Command, JsonSerializer.Serialize(State.Input));
            }
            finally
            {
                await sandbox.UnloadAsync(Id.Brain.Value, appKey);
            }
            steps.AddRange((await grants.Steps(appKey)).Select(step => step.Allowed ? $"call {step.Call}" : $"refused {step.Call}"));
            steps.Add("run");
            steps.Add("unload");
            return result.Error is { Length: > 0 } || !result.Handled
                ? await Finish(steps, null, result.Error ?? "The sandbox did not run the block.")
                : await Finish(steps, CodeBlockSource.Output(result.ResultJson), null);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or KeyNotFoundException or TimeoutException)
        {
            return await Finish(steps, null, error.Message);
        }
        finally
        {
            await grants.Release(appKey);
        }
    }

    private CompiledAppPackage Compile() => compiler.Compile(CodeBlockSource.Package(CodeBlockSource.AppKey(Id.Key), State.Title, State.Source));

    private static string Refusal(CompiledAppPackage compiled)
        => "The code does not compile within the sandbox's rules: " + string.Join("; ", compiled.Errors.Take(5));

    private async Task<CodeBlockRunView> Finish(IEnumerable<string> steps, string? output, string? error)
    {
        await Save(State with { Status = error is null ? CodeBlockStatus.Completed : CodeBlockStatus.Failed, Steps = [.. steps], Output = output, Error = error, PlanId = null, Needs = _needs ?? State.Needs },
            new CodeBlockRunDone(Id.Key, error is null, output, error));
        return View();
    }

    private CodeBlockRunView View() => new(State.RunId, State.Status, State.Steps, State.Output, State.Error, State.Needs, State.PlanId);

    private IClientGrants Grants() => Context.Get<IClientGrants>(new DigitalBrain.NeuronId(Id.Brain, "client.grants", IClientGrants.DefaultKey));

    private IConfirmationRequests Confirmations() => Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    private static string Short(string contract) => contract[(contract.LastIndexOf('.') + 1)..];
}

// How a block becomes a one-command app the sandbox can load.
internal static class CodeBlockSource
{
    internal const string Command = "run";

    internal static string AppKey(string runId) => IClientGrants.CodeBlockAppPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))[..12];

    // A block is `Block.Run(string input)` returning a string, or `Block.Run(string input, ISandboxBrain brain)` returning a string or a
    // Task<string> when it calls neurons through the brain client.
    internal static AppPackage Package(string appKey, string title, string source)
    {
        var call = source.Contains("ISandboxBrain", StringComparison.Ordinal) ? "global::Block.Run(input, Brain)" : "global::Block.Run(input)";
        var wrapper = $$"""
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            namespace CodeBlocks;

            public sealed record CodeBlockState;
            public sealed record CodeBlockSettings;

            [App("{{appKey}}", Title = "Code block")]
            public sealed class CodeBlockApp : BrainApp<CodeBlockState, CodeBlockSettings>
            {
                [OnCommand("{{Command}}", Description = "Run the block once.")]
                public Task<string> Run(string input) => CodeBlockResult.Of({{call}});
            }

            internal static class CodeBlockResult
            {
                public static Task<string> Of(string text) => Task.FromResult(text);
                public static Task<string> Of(Task<string> pending) => pending;
            }
            """;
        AppSourceFile[] files = [new("CodeBlockApp.cs", wrapper), new("Block.cs", source)];
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wrapper + "\n" + source)));
        return new AppPackage(appKey, "1.0.0", string.IsNullOrWhiteSpace(title) ? "Code block" : title, "A code block run once in the sandbox.",
            "code", "CodeBlock", "brain", files, AppPackageNeeds.Empty, "{}", digest);
    }

    // The command's return value comes back as JSON; a string result reads as the text itself.
    internal static string? Output(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) { return null; }
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : document.RootElement.GetRawText();
        }
        catch (JsonException) { return resultJson; }
    }
}
