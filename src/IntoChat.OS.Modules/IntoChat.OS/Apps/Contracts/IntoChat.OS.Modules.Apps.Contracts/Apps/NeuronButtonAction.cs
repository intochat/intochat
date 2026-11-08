namespace IntoChat.OS.Modules.Apps;

// A view button that targets a neuron other than its app, as the presentation writes it for the shell:
// neuron://{key}/{operation}?a={arg}&a={arg}. The edge routes it under the clicking person's caller; only the targets it names
// are honoured (today the confirmations inbox's Resolve).
public static class NeuronButtonAction
{
    public const string Scheme = "neuron://";

    public static string Format(string key, string operation, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var query = arguments.Count == 0 ? "" : "?" + string.Join('&', arguments.Select(a => "a=" + Uri.EscapeDataString(a)));
        return $"{Scheme}{key}/{Uri.EscapeDataString(operation)}{query}";
    }

    public static bool TryParse(string? action, out string key, out string operation, out string[] arguments)
    {
        key = operation = "";
        arguments = [];
        if (action is null || !action.StartsWith(Scheme, StringComparison.Ordinal)) { return false; }
        var rest = action[Scheme.Length..];
        var queryAt = rest.IndexOf('?', StringComparison.Ordinal);
        var path = queryAt < 0 ? rest : rest[..queryAt];
        var slash = path.LastIndexOf('/');
        if (slash <= 0 || slash == path.Length - 1) { return false; }
        key = path[..slash];
        operation = Uri.UnescapeDataString(path[(slash + 1)..]);
        arguments = queryAt < 0
            ? []
            : [.. rest[(queryAt + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => p.StartsWith("a=", StringComparison.Ordinal)).Select(p => Uri.UnescapeDataString(p[2..]))];
        return true;
    }
}
