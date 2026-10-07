using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Files;

namespace DigitalBrain.Apps;

// The key of a deployed app program: {brain}/csharp/app-{digest of app, generation and bundle path}.
// The name is a digest, so nothing but the app that knows its own name and generation can compute
// another app's program key; the owning-app check on the file stays the actual guard.
internal static class AppProgramKey
{
    public static string For(string appKey, int generation, string path)
    {
        var marker = appKey.IndexOf("/apps/", StringComparison.Ordinal);
        if (marker <= 0) { throw new ArgumentException("An app key is {brain}/apps/{name}.", nameof(appKey)); }
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(appKey + "\0" + generation + "\0" + path)))[..32];
        return CSharpFileKeys.For(appKey[..marker], "app-" + digest);
    }
}
