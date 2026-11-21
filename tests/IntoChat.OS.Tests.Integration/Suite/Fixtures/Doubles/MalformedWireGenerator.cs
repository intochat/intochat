namespace IntoChat.OS.Tests.E2E.Fixtures.Doubles;

/// <summary>
/// Helper generator for wire-protocol fuzzing, malformed JSON, and host-reserved 'by' field injection.
/// </summary>
public static class MalformedWireGenerator
{
    /// <summary>Produces a truncated JSON string missing closing braces.</summary>
    public static string TruncatedJson() => "{\"alias\":\"app.test\",\"generation\":1,\"view\":{";

    /// <summary>Produces a JSON string with an invalid/unregistered node kind.</summary>
    public static string InvalidNodeKindJson() =>
        "{\"alias\":\"app.test\",\"generation\":1,\"view\":{\"kind\":\"UnicornWidget\",\"key\":\"w-1\"}}";

    /// <summary>Produces a JSON string with host-reserved 'by' injected inside an emit's args.</summary>
    public static string InjectedByArgsJson() =>
        "{\"alias\":\"app.test\",\"generation\":1,\"view\":{\"kind\":\"Button\",\"key\":\"btn-1\",\"emit\":{\"alias\":\"app.test\",\"signal\":\"press\",\"args\":{\"by\":\"spoofed@domain.com\"}}}}";

    /// <summary>Produces corrupted binary payload (non-UTF8 byte noise).</summary>
    public static byte[] CorruptedBinaryPayload() => [0xFF, 0xFE, 0x00, 0x01, 0x80, 0xC0];

    /// <summary>Produces a JSON string with type mismatch (e.g. string passed for integer field).</summary>
    public static string TypeMismatchJson(string propertyName = "Number") =>
        $"{{\"{propertyName}\":\"not-a-number\",\"Message\":\"type mismatch test\"}}";

    /// <summary>Produces an unclosed JSON string with truncated syntax.</summary>
    public static string TruncatedSignalJson() => "{\"Number\": 42, \"Message\": \"incomp";
}
