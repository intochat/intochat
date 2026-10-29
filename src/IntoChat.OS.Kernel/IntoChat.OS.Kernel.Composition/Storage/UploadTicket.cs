using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace IntoChat.OS.Kernel.Composition.Storage;

/// <summary>
/// HMAC ticket (Data Protection) bound to a brain, storage, folder, name and optional overwrite <see cref="PlanId"/>.
/// Lives for ten minutes. A ticket without <see cref="PlanId"/> cannot overwrite.
/// </summary>
internal sealed record UploadTicket(string Brain, string Storage, string Folder, string Name, string? PlanId, DateTimeOffset Expires)
{
    internal const string Purpose = "IntoChat.OS.Storage.Content.UploadTicket";

    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    internal string Protect(IDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        return protector.Protect(JsonSerializer.Serialize(this));
    }

    internal static UploadTicket? Open(IDataProtector protector, string? token, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);
        if (string.IsNullOrEmpty(token)) { return null; }
        try
        {
            var ticket = JsonSerializer.Deserialize<UploadTicket>(protector.Unprotect(token));
            return ticket is { Brain.Length: > 0 } && ticket.Expires > clock.GetUtcNow() ? ticket : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}
