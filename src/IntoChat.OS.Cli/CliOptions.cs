using System.Globalization;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Cli;

public sealed record CliOptions(string Verb, string[] Arguments, Uri Base, string Brain, int Limit, CliProof? Proof)
{
    public const string DefaultBase = "http://localhost:5080";

    private static readonly string[] Named = ["--base", "--brain", "--limit"];

    public static CliOptions Parse(string[] args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        string Value(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : environment("INTOCHAT_" + name.TrimStart('-').ToUpperInvariant()) ?? fallback;
        }
        var positional = args.Where((arg, i) => !Named.Contains(arg) && (i == 0 || !Named.Contains(args[i - 1]))).ToArray();
        var baseText = Value("--base", DefaultBase);
        if (!Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"--base must be an http or https address, not '{baseText}'.");
        }
        if (!int.TryParse(Value("--limit", "20"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit <= 0)
        {
            throw new ArgumentException("--limit must be a positive number.");
        }
        var proof = CliProof.From(environment);
        // A password or a session over plain http would cross the network in the clear: only this machine may be reached that way.
        if (proof is { Kind: not CliProofKind.Presence } && baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            throw new ArgumentException($"A {proof.Kind.ToString().ToLowerInvariant()} credential goes to another machine over https only, not to {baseUri}.");
        }
        return new(positional.FirstOrDefault() ?? "", [.. positional.Skip(1)], baseUri, Value("--brain", "owner"), limit, proof);
    }
}

public enum CliProofKind { Presence, Basic, Session }

// How the person behind the command proves who they are at the edge. The presence token is the local proof (the person at this
// machine); a Basic credential or a session cookie is how a signed-in person reaches a remote install.
public sealed record CliProof(CliProofKind Kind, string Value)
{
    public const string PresenceVariable = "INTOCHAT_PRESENCE";
    public const string BasicVariable = "INTOCHAT_BASIC";
    public const string SessionVariable = "INTOCHAT_SESSION";

    public static CliProof? From(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment(PresenceVariable) is { Length: > 0 } presence) { return new(CliProofKind.Presence, presence); }
        if (environment(BasicVariable) is { Length: > 0 } basic)
        {
            if (!basic.Contains(':', StringComparison.Ordinal)) { throw new ArgumentException($"{BasicVariable} must be user:password."); }
            return new(CliProofKind.Basic, basic);
        }
        if (environment(SessionVariable) is { Length: > 0 } session)
        {
            if (!session.Contains('=', StringComparison.Ordinal)) { throw new ArgumentException($"{SessionVariable} must be the session cookie as name=value."); }
            return new(CliProofKind.Session, session);
        }
        return null;
    }

    // The request headers that carry this proof, by name and value.
    public IEnumerable<KeyValuePair<string, string>> Headers() => Kind switch
    {
        CliProofKind.Presence => [new(PresenceProof.HeaderName, Value)],
        CliProofKind.Basic => [new("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Value)))],
        CliProofKind.Session => [new("Cookie", Value)],
        _ => [],
    };

    public override string ToString() => $"{Kind} proof";
}
