using System.Text;
using System.Text.Json;
using DigitalBrain.UI;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire.Journeys;

public static class ViewComparison
{
    public static string? Diff(View expected, View actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var expectedJson = ShellAppJourneys.CanonicalJson(expected);
        var actualJson = ShellAppJourneys.CanonicalJson(actual);
        if (string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
        {
            return null;
        }

        using var expectedDoc = JsonDocument.Parse(expectedJson);
        using var actualDoc = JsonDocument.Parse(actualJson);
        var pointer = FindFirstDiffPointer(expectedDoc.RootElement, actualDoc.RootElement, "") ?? "/";

        var expectedLines = expectedJson.Split('\n');
        var actualLines = actualJson.Split('\n');
        var max = Math.Max(expectedLines.Length, actualLines.Length);
        var diffLine = 1;
        var expectedLine = "";
        var actualLine = "";
        for (var i = 0; i < max; i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "<eof>";
            var a = i < actualLines.Length ? actualLines[i] : "<eof>";
            if (!string.Equals(e, a, StringComparison.Ordinal))
            {
                diffLine = i + 1;
                expectedLine = e;
                actualLine = a;
                break;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"View drift at JSON pointer '{pointer}' (line {diffLine}):");
        sb.AppendLine($"- expected: {expectedLine}");
        sb.Append($"- actual:   {actualLine}");
        return sb.ToString();
    }

    private static string? FindFirstDiffPointer(JsonElement expected, JsonElement actual, string currentPointer)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return currentPointer.Length == 0 ? "/" : currentPointer;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var expectedProps = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                    var actualProps = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

                    foreach (var prop in expected.EnumerateObject())
                    {
                        var childPointer = $"{currentPointer}/{EscapePointerSegment(prop.Name)}";
                        if (!actualProps.TryGetValue(prop.Name, out var actualVal))
                        {
                            return childPointer;
                        }
                        if (FindFirstDiffPointer(prop.Value, actualVal, childPointer) is { } diff)
                        {
                            return diff;
                        }
                    }

                    foreach (var prop in actual.EnumerateObject())
                    {
                        if (!expectedProps.ContainsKey(prop.Name))
                        {
                            return $"{currentPointer}/{EscapePointerSegment(prop.Name)}";
                        }
                    }

                    return null;
                }

            case JsonValueKind.Array:
                {
                    var expectedItems = expected.EnumerateArray().ToArray();
                    var actualItems = actual.EnumerateArray().ToArray();
                    var minLen = Math.Min(expectedItems.Length, actualItems.Length);
                    for (var i = 0; i < minLen; i++)
                    {
                        var childPointer = $"{currentPointer}/{i}";
                        if (FindFirstDiffPointer(expectedItems[i], actualItems[i], childPointer) is { } diff)
                        {
                            return diff;
                        }
                    }

                    if (expectedItems.Length != actualItems.Length)
                    {
                        return $"{currentPointer}/{minLen}";
                    }

                    return null;
                }

            default:
                return string.Equals(expected.GetRawText(), actual.GetRawText(), StringComparison.Ordinal)
                    ? null
                    : (currentPointer.Length == 0 ? "/" : currentPointer);
        }
    }

    private static string EscapePointerSegment(string segment)
        => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
