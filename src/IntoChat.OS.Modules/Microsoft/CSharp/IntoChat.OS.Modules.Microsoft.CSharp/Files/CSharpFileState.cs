using System.Runtime.Serialization;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Files;

// A file's record: its name and purpose, its program, its settings and the caller who started it. The runtime speaks for that
// caller as the owner of the file's one-behavior program.
[GenerateSerializer, Alias("microsoft.csharp.file-state")]
[ExportableState]
internal sealed record CSharpFileState
{
    [Id(0)] public string Program { get; init; } = "";
    [Id(1), LegacyShape] public Dictionary<string, string>? LegacySettings { get; init; }
    [Id(2)] public string? Hash { get; init; }
    [Id(3)] public bool Wanted { get; init; }
    [Id(4)] public CallerContext? Owner { get; init; }
    [Id(5)] public string Name { get; init; } = "";
    [Id(6)] public string Purpose { get; init; } = "";
    [Id(7)] public DateTimeOffset? Created { get; init; }
    [Id(8)] public StringEntry[] SettingEntries { get; init; } = [];

    [IgnoreDataMember]
    public IReadOnlyDictionary<string, string> Settings
    {
        get => StringEntry.ToMap(SettingEntries, LegacySettings);
        init { SettingEntries = StringEntry.From(value); LegacySettings = null; }
    }
}
