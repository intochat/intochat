using IntoChat.OS.Framework;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Flutter.Chat;

// The chat observers reconcile from state they own, so their per-source watermark lives in memory only.
// History comes from the silo journal, so pulling never activates the source.
internal static class SignalPull
{
    public const int PageSize = 256;

    public static async Task<long> Head(ISignalJournal journal, GrainId source) => (await SignalPages.Read(journal, source, 0, 0)).Last;

    public static async Task<(Signal[] Signals, long Watermark)> After(ISignalJournal journal, GrainId source, long watermark, ILogger logger)
    {
        var read = new List<Signal>();
        while (true)
        {
            var page = await SignalPages.Read(journal, source, watermark, PageSize);
            if (page.Floor > watermark)
            {
                logger.LogWarning("Signals of {Source} after {Watermark} up to {Floor} were dropped; jumping ahead.",
                    source, watermark, page.Floor);
                watermark = page.Floor;
            }
            read.AddRange(page.Signals);
            if (page.Signals.Length < PageSize) { return ([.. read], Math.Max(watermark, page.Last)); }
            watermark = page.Sequences[^1];
        }
    }
}
