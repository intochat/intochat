using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Flutter.Chat;

// The chat observers reconcile from state they own, so their per-source watermark lives in memory only.
internal static class SignalPull
{
    public const int PageSize = 256;

    public static async Task<long> Head(INeuron source) => (await source.Read(0, 0)).Last;

    public static async Task<(Signal[] Signals, long Watermark)> After(INeuron source, long watermark, ILogger logger)
    {
        var read = new List<Signal>();
        while (true)
        {
            var page = await source.Read(watermark, PageSize);
            if (page.Floor > watermark)
            {
                logger.LogWarning("Signals of {Source} after {Watermark} up to {Floor} were dropped; jumping ahead.",
                    source.GetGrainId(), watermark, page.Floor);
                watermark = page.Floor;
            }
            read.AddRange(page.Signals);
            if (page.Signals.Length < PageSize) { return ([.. read], Math.Max(watermark, page.Last)); }
            watermark = page.Sequences[^1];
        }
    }
}
