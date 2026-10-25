using IntoChat.OS;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Files.Signals;

// Published on every durable change of a file: source, settings, run, trigger or subscriptions.
[GenerateSerializer, Alias("microsoft.csharp.file-changed")]
public sealed record CSharpFileChanged([property: Id(0)] string FileId) : Signal;
