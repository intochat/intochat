using DigitalBrain;

namespace DigitalBrain.Microsoft.CSharp.Files.Signals;

// The grain's own doorbell: a signal arrived for a subscription. Not part of the public contract.
[GenerateSerializer, Alias("microsoft.csharp.pending-arrived")]
internal sealed record CSharpPendingArrived([property: Id(0)] string FileId) : Signal;
