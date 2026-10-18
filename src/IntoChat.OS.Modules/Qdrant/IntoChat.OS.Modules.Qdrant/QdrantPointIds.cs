using System.Security.Cryptography;
using System.Text;

namespace IntoChat.OS.Qdrant;

internal static class QdrantPointIds
{
    public static Guid For(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
}
