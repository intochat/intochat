using System.Security.Cryptography;

namespace DigitalBrain.Platform.Keys;

// Master key recovery (spec Shape): a random polynomial per byte over GF(2^8) (polynomial 0x11D, the field
// ReedSolomon uses); share i is its value at x = i, and any `threshold` shares interpolate back to x = 0.
internal static class Shamir
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static Shamir()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) { x ^= 0x11D; }
        }
        for (int i = 255; i < 512; i++) { Exp[i] = Exp[i - 255]; }
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];

    public static byte[][] Split(byte[] secret, int threshold, int shares)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, shares);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shares, 255);
        var result = Enumerable.Range(1, shares).Select(x => { var share = new byte[secret.Length + 1]; share[0] = (byte)x; return share; }).ToArray();
        var coefficients = new byte[threshold];
        for (int b = 0; b < secret.Length; b++)
        {
            coefficients[0] = secret[b];
            RandomNumberGenerator.Fill(coefficients.AsSpan(1));
            foreach (var share in result)
            {
                byte y = 0;
                for (int c = threshold - 1; c >= 0; c--) { y = (byte)(Mul(y, share[0]) ^ coefficients[c]); }
                share[b + 1] = y;
            }
        }
        CryptographicOperations.ZeroMemory(coefficients);
        return result;
    }

    public static byte[] Combine(byte[][] shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        var secret = new byte[shares[0].Length - 1];
        for (int b = 0; b < secret.Length; b++)
        {
            byte value = 0;
            for (int i = 0; i < shares.Length; i++)
            {
                byte basis = 1;
                for (int j = 0; j < shares.Length; j++)
                {
                    if (i != j) { basis = Mul(basis, Div(shares[j][0], (byte)(shares[j][0] ^ shares[i][0]))); }
                }
                value ^= Mul(shares[i][b + 1], basis);
            }
            secret[b] = value;
        }
        return secret;
    }
}
