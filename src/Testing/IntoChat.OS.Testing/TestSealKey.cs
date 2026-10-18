namespace IntoChat.OS.Testing;

// The checked-in seal key for golden segments and storage fixtures (clean-core W1, section 4.4). It is public by
// design and seals nothing but test data: never a custody key, never configured on a hosted brain. As a segment key it
// seals journal frames directly; as the configured master key it roots the custody that wraps each fixture brain's key.
public static class TestSealKey
{
    public const string Base64 = "U+WsLw3/Ru1PYWOrONv59Ske1XTCXwWKgPIosLdGkOA=";

    public static byte[] Bytes() => Convert.FromBase64String(Base64);
}
