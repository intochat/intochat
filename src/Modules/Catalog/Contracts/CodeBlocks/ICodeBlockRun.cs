using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.CodeBlocks;

// A synapse of the CodeBlock kind: a small piece of generated C# that runs once in the sandbox container, never in the kernel. The
// source is a static class Block with `public static string Run(string input)`; the OS wraps it as a one-command app, compiles it under
// the app boundary (no file, network or process access), deploys it to the sandbox, runs it with the input, and unloads it.
// Its code may call neurons through the brain client (Brain.Get<T>(key)); the contracts it uses are its needs, approved once by a
// person (remembered by their hash), and the kernel refuses any other.
public enum CodeBlockStatus { Running, Completed, Failed, Waiting, Cancelled }

// Needs are the contracts the block's code reaches through the brain client (inferred); PlanId is the approval it waits for.
public sealed record CodeBlockRunView(string RunId, CodeBlockStatus Status, string[] Steps, string? Output, string? Error, string[]? Needs = null, string? PlanId = null);

[Signal("codeblock.run-started")]
public sealed record CodeBlockRunStarted(string RunId, string Title) : DigitalBrain.Signal;

[Signal("codeblock.run-waiting")]
public sealed record CodeBlockRunWaiting(string RunId, string PlanId, string[] Needs) : DigitalBrain.Signal;

[Signal("codeblock.run-done")]
public sealed record CodeBlockRunDone(string RunId, bool Succeeded, string? Output, string? Error, bool Cancelled = false) : DigitalBrain.Signal;

[NeuronAlias("codeblock.run"), DefaultNeuronType("codeblock.run")]
[Description("Run a small piece of C# in the sandbox container. source declares `public static class Block { public static string Run(string input) { ... } }`; only safe APIs compile.")]
public interface ICodeBlockRun
{
    const string KeyPattern = "codeblocks/{new short id}";

    [Description("Compile source, run Block.Run(input) in the sandbox, and return the output. A run starts once; starting again returns it as it is.")]
    Task<CodeBlockRunView> Start(string title, string source, string input);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<CodeBlockRunView?> Read();

    [Description("Stop this run while it waits for approval: it withdraws the question and ends Cancelled with the reason. A run that already ended stays as it is.")]
    Task<CodeBlockRunView?> Cancel(string reason);
}
