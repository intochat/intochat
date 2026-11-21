using System.Text.Json;

namespace IntoChat.OS.Tests.E2E.Fixtures.Doubles;

/// <summary>
/// Helper generator for states and payloads around the strict 2 MB cap (2,097,152 UTF-8 bytes).
/// Used for Boundary Value Analysis (BVA) across Screen views and UnitMachine states.
/// </summary>
public static class OversizedPayloadBuilder
{
    public const int MaxStateBytes = 2 * 1024 * 1024; // 2,097,152 bytes

    /// <summary>Generates an exact ASCII string of specified length in UTF-8 bytes.</summary>
    public static string CreateExactString(int targetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetBytes);
        return new string('X', targetBytes);
    }

    /// <summary>Generates a string payload at an exact offset from MaxStateBytes.</summary>
    public static string CreateAtOffset(int offset)
    {
        return CreateExactString(MaxStateBytes + offset);
    }

    /// <summary>Generates a JSON object payload of approximate target byte length.</summary>
    public static string CreateJsonPayload(int targetBytes)
    {
        var rawLength = Math.Max(0, targetBytes - 20);
        var raw = CreateExactString(rawLength);
        return JsonSerializer.Serialize(new { payload = raw });
    }

    /// <summary>
    /// Helper record representing a state container for unit state serialization limit testing.
    /// </summary>
    public sealed record ExactByteState(string Data);

    /// <summary>
    /// Generates an <see cref="ExactByteState"/> whose UTF-8 JSON serialization is exactly the target byte length.
    /// JSON structure is: {"Data":"..."} which adds 11 bytes of JSON framing ({"Data":""} = 11 chars/bytes).
    /// </summary>
    public static ExactByteState CreateStateOfSerializedLength(int targetBytes)
    {
        const int framingOverhead = 11; // {"Data":""}
        var stringLength = Math.Max(0, targetBytes - framingOverhead);
        return new ExactByteState(new string('A', stringLength));
    }
}
