namespace DigitalBrain.Microsoft.CSharp.Files;

[GenerateSerializer, Alias("microsoft.csharp.file-status")]
public enum CSharpFileStatus { Stopped, Starting, Running, Exited, Disarmed }

// A behavior as its brain sees it: the program, the assembly it compiled to, and the status its brain
// runtime last heard from the sandbox session.
[GenerateSerializer, Alias("microsoft.csharp.file-snapshot")]
public sealed record CSharpFileSnapshot(
    [property: Id(0)] string Id,
    [property: Id(1)] string Source,
    [property: Id(2)] IReadOnlyDictionary<string, string> Settings,
    [property: Id(3)] string? Hash,
    [property: Id(4)] CSharpFileStatus Status,
    [property: Id(5)] int? ExitCode,
    [property: Id(6)] bool ShouldRun,
    [property: Id(7)] int Failures);

[GenerateSerializer, Alias("microsoft.csharp.subscription-view")]
public sealed record CSharpSubscriptionView(
    [property: Id(0)] string Neuron,
    [property: Id(1)] string Signal,
    [property: Id(2)] int Pending,
    [property: Id(3)] long Delivered,
    [property: Id(4)] long Dropped);

