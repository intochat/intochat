using System.Reflection;
using System.Text;

namespace IntoChat.OS.Kernel.Install;

/// <summary>
/// Loads the declarative Pulumi YAML templates (<c>deploy/*.yaml</c>) and POSIX Azure CLI retry helper
/// (<c>scripts/az-retry.sh</c>) embedded into <c>IntoChat.OS.Kernel</c>.
/// </summary>
internal static class PulumiAssets
{
    private const string ResourcePrefix = "IntoChat.OS.Kernel.Install.Assets.";

    private static readonly Lazy<string> PulumiYamlField = new(() => LoadEmbedded("Pulumi.yaml"));
    private static readonly Lazy<string> StackConfigYamlField = new(() => LoadEmbedded("Pulumi.stack.yaml"));
    private static readonly Lazy<string> CommandStopYamlField = new(() => LoadEmbedded("command-stop.yaml"));
    private static readonly Lazy<string> CommandStartYamlField = new(() => LoadEmbedded("command-start.yaml"));
    private static readonly Lazy<string> AzRetryScriptField = new(() => LoadEmbedded("az-retry.sh"));
    private static readonly Lazy<string> AzRetryFunctionField = new(() => ExtractMarkedBlock(AzRetryScriptField.Value, "az_retry"));
    private static readonly Lazy<string> StopRevisionsBodyField = new(() => ExtractMarkedBlock(AzRetryScriptField.Value, "stop_revisions_body"));
    private static readonly Lazy<string> StartRevisionBodyField = new(() => ExtractMarkedBlock(AzRetryScriptField.Value, "start_revision_body"));

    public static string PulumiYamlTemplate => PulumiYamlField.Value;

    public static string StackConfigYamlTemplate => StackConfigYamlField.Value;

    public static string CommandStopYamlTemplate => CommandStopYamlField.Value;

    public static string CommandStartYamlTemplate => CommandStartYamlField.Value;

    public static string AzRetryScript => AzRetryScriptField.Value;

    public static string AzRetryFunction => AzRetryFunctionField.Value;

    public static string StopRevisionsBody => StopRevisionsBodyField.Value;

    public static string StartRevisionBody => StartRevisionBodyField.Value;

    /// <summary>
    /// Escapes literal <c>${</c> sequences (such as POSIX shell parameter expansions) as <c>$${</c>
    /// so Pulumi YAML passes them through verbatim instead of resolving them as Pulumi expressions.
    /// </summary>
    public static string EscapePulumiYaml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("${", "$${", StringComparison.Ordinal);
    }

    public static string Indent(string text, int spaces)
    {
        ArgumentNullException.ThrowIfNull(text);
        var pad = new string(' ', spaces);
        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var sb = new StringBuilder(text.Length + (lines.Length * spaces));
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }
            if (lines[i].Length > 0)
            {
                sb.Append(pad);
                sb.Append(lines[i]);
            }
        }
        return sb.ToString();
    }

    internal static string ExtractMarkedBlock(string script, string marker)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

        var lines = script.ReplaceLineEndings("\n").Split('\n');
        var beginToken = $"# BEGIN {marker}";
        var endToken = $"# END {marker}";
        var startIndex = -1;
        var endIndex = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (startIndex < 0 && string.Equals(trimmed, beginToken, StringComparison.Ordinal))
            {
                startIndex = i + 1;
                continue;
            }
            if (startIndex >= 0 && string.Equals(trimmed, endToken, StringComparison.Ordinal))
            {
                endIndex = i;
                break;
            }
        }

        if (startIndex < 0 || endIndex <= startIndex)
        {
            throw new InvalidOperationException($"Declarative script marker '{marker}' was not found in embedded asset 'az-retry.sh'.");
        }

        var slice = lines[startIndex..endIndex];
        var minIndent = int.MaxValue;
        foreach (var line in slice)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }
            if (indent < minIndent)
            {
                minIndent = indent;
            }
        }
        if (minIndent == int.MaxValue)
        {
            minIndent = 0;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < slice.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }
            var line = slice[i];
            sb.Append(line.Length >= minIndent ? line[minIndent..] : line);
        }
        return sb.ToString();
    }

    private static string LoadEmbedded(string fileName)
    {
        var assembly = typeof(PulumiAssets).Assembly;
        var resourceName = ResourcePrefix + fileName;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded declarative IaC asset '{resourceName}' was not found in assembly '{assembly.GetName().Name}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
