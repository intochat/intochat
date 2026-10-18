namespace IntoChat.OS.Microsoft.Roslyn;

[GenerateSerializer]
[Alias("coding.member-query")]
public sealed record MemberQuery([property: Id(0)] string SymbolId);
