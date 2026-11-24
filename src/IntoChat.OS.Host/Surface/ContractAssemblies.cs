namespace IntoChat.OS.Host.Surface;

// The assembly-name prefixes a host passes to LoadedContracts to find neuron contracts it has not loaded yet: the OS and
// its modules (IntoChat.OS*), the components (IntoChat.<Name>, Aspire is the first) and DigitalBrain. LoadedContracts
// matches by StartsWith, so "IntoChat.Aspire" also admits its .Contracts assembly.
public static class ContractAssemblies
{
    public static IReadOnlyList<string> Prefixes { get; } = ["IntoChat.OS", "IntoChat.Aspire", "DigitalBrain"];

    public static string[] PrefixArray() => [.. Prefixes];
}
