using System.Security.Cryptography;
using System.Text;

namespace IntoChat.Cloud.Mail;

public static class SystemShard
{
    public static string BrainOf(string key) =>
        "system-" + (SHA256.HashData(Encoding.UTF8.GetBytes(key))[0] & 0x0F).ToString("X2");
}
