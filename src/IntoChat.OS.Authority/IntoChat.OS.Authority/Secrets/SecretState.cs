using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Secrets;

[GenerateSerializer, Alias("mydata.state")]
internal sealed class SecretsState
{
    [Id(0)] public string Owner { get; set; } = "";
    [Id(1)] public string WrappedOwnerKey { get; set; } = "";
    [Id(2)] public Dictionary<string, SecretRecord> Fields { get; set; } = [];
}

[GenerateSerializer, Alias("mydata.field-record")]
internal sealed class SecretRecord
{
    [Id(0)] public string FieldPath { get; set; } = "";
    [Id(1)] public FieldKind Kind { get; set; }
    [Id(2)] public bool IsSecret { get; set; }
    [Id(3)] public string Label { get; set; } = "";
    [Id(4)] public bool IsSet { get; set; }
    [Id(5)] public string SealedValue { get; set; } = "";
    [Id(7)] public string SealedSecret { get; set; } = "";
}
