using System.Security.Cryptography;
using System.Text;

namespace DigitalBrain.Apps;

// The grain key of a deployed app program: a digest of app key, program generation and bundle path.
// Hashing the brain scope away is deliberate, so no key grammar lets a script address another app's
// program; whoever needs the key recomputes it from what the app already knows.
internal static class AppProgramKey
{
    public static string For(string appKey, int generation, string path)
        => "app-csharp-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(appKey + "\0" + generation + "\0" + path)));
}
