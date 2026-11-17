using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntoChat.OS.Modules.Apps.CodeBlocks;

internal sealed record CodeBlockRunState(string RunId, CodeBlockStatus Status, string[] Steps, string? Output, string? Error)
{
    public CodeBlockRunState() : this("", CodeBlockStatus.Running, [], null, null) { }
}

// One code block run (key = run id): wrap, compile, deploy to the sandbox, run, unload; each step is recorded and the outcome
// published, so the runs index shows it. The block never runs in the kernel: the bridge reaches the sandbox container.
[DigitalBrain.Neurons.Neuron("codeblock.run")]
internal sealed class CodeBlockRunNeuron(AppPackageCompiler compiler, ISandboxRuntimeBridge sandbox) : DigitalBrain.Neurons.Neuron<CodeBlockRunState>, ICodeBlockRun
{
    public async Task<CodeBlockRunView> Start(string title, string source, string input)
    {
        if (State.RunId.Length > 0) { return View(); }
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        await Save(new CodeBlockRunState() with { RunId = Id.Key }, new CodeBlockRunStarted(Id.Key, string.IsNullOrWhiteSpace(title) ? "code block" : title));

        var steps = new List<string>();
        var appKey = CodeBlockSource.AppKey(Id.Key);
        try
        {
            var compiled = compiler.Compile(CodeBlockSource.Package(appKey, title, source));
            steps.Add("compile");
            if (!compiled.Succeeded || compiled.EnrichedPackage is null)
            {
                return await Finish(steps, null, "The code does not compile within the sandbox's rules: " + string.Join("; ", compiled.Errors.Take(5)));
            }

            await sandbox.DeployPackageAsync(Id.Brain.Value, compiled.EnrichedPackage, compiled.AssemblyBytes, null, null);
            steps.Add("deploy");
            SandboxDispatchResult result;
            try
            {
                result = await sandbox.DispatchCommandAsync(Id.Brain.Value, appKey, CodeBlockSource.Command, JsonSerializer.Serialize(input ?? ""));
                steps.Add("run");
            }
            finally
            {
                // The block is gone from the sandbox before its outcome is recorded.
                await sandbox.UnloadAsync(Id.Brain.Value, appKey);
                steps.Add("unload");
            }
            return result.Error is { Length: > 0 } || !result.Handled
                ? await Finish(steps, null, result.Error ?? "The sandbox did not run the block.")
                : await Finish(steps, CodeBlockSource.Output(result.ResultJson), null);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException or KeyNotFoundException or TimeoutException)
        {
            return await Finish(steps, null, error.Message);
        }
    }

    public Task<CodeBlockRunView?> Read() => Task.FromResult(State.RunId.Length > 0 ? View() : null);

    private async Task<CodeBlockRunView> Finish(List<string> steps, string? output, string? error)
    {
        await Save(State with { Status = error is null ? CodeBlockStatus.Completed : CodeBlockStatus.Failed, Steps = [.. steps], Output = output, Error = error },
            new CodeBlockRunDone(Id.Key, error is null, output, error));
        return View();
    }

    private CodeBlockRunView View() => new(State.RunId, State.Status, State.Steps, State.Output, State.Error);
}

// How a block becomes a one-command app the sandbox can load.
internal static class CodeBlockSource
{
    internal const string Command = "run";

    internal static string AppKey(string runId) => "codeblock-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))[..12];

    internal static AppPackage Package(string appKey, string title, string source)
    {
        var wrapper = $$"""
            using IntoChat.Sdk;

            namespace CodeBlocks;

            public sealed record CodeBlockState;
            public sealed record CodeBlockSettings;

            [App("{{appKey}}", Title = "Code block")]
            public sealed class CodeBlockApp : BrainApp<CodeBlockState, CodeBlockSettings>
            {
                [OnCommand("{{Command}}", Description = "Run the block once.")]
                public string Run(string input) => global::Block.Run(input);
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
