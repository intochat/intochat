using DigitalBrain;

namespace IntoChat.Cloud.Mail;

internal static class MailIds
{
    public static NeuronId Of(string type, string fullKey)
    {
        var slash = fullKey.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) { throw new ArgumentException($"'{fullKey}' names no brain.", nameof(fullKey)); }
        return new NeuronId(new BrainId(fullKey[..slash]), type, fullKey[(slash + 1)..]);
    }
}
