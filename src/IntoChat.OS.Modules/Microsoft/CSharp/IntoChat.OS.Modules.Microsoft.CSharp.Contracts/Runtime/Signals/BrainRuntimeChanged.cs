using IntoChat.OS;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;

// Published on every durable change of a brain runtime: its behaviors, subscriptions or session.
[GenerateSerializer, Alias("microsoft.csharp.runtime-changed")]
public sealed record BrainRuntimeChanged([property: Id(0)] string RuntimeId) : Signal;
