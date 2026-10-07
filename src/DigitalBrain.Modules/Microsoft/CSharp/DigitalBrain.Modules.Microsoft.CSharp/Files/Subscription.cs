using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp;

// One durable subscription a script declared by opening a signal stream. Pending[i] is signal number
// Delivered + 1 + i: delivery is at-least-once, so a batch stays here until it is acknowledged and a
// drain whose consumer died is simply drained again. Past the cap the oldest signal is dropped and
// counted, never silently. Arrays, not interface lists: grain storage round-trips this state through JSON.
[GenerateSerializer, Alias("microsoft.csharp.subscription")]
internal sealed record Subscription(
    [property: Id(0)] string Neuron,
    [property: Id(1)] string Signal,
    [property: Id(2)] string[] Pending,
    [property: Id(3)] long Delivered,
    [property: Id(4)] long Dropped)
{
    public long Watermark => Delivered + Pending.Length;

    public Subscription Arrive(string json, int cap)
        => Pending.Length < cap
            ? this with { Pending = [.. Pending, json] }
            : this with { Pending = [.. Pending.Skip(1), json], Delivered = Delivered + 1, Dropped = Dropped + 1 };

    public Subscription Acknowledge(long watermark)
    {
        var trim = (int)Math.Clamp(watermark - Delivered, 0, Pending.Length);
        return trim == 0 ? this : this with { Pending = Pending[trim..], Delivered = Delivered + trim };
    }
}

[GenerateSerializer, Alias("microsoft.csharp.pending-arrived")]
internal sealed record CSharpPendingArrived([property: Id(0)] string FileId) : Signal;
