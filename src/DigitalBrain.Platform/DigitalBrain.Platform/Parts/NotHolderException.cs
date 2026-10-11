namespace DigitalBrain.Platform.Parts;

// T20-12: the machine a call was forwarded to does not hold the part (any more); nothing was executed.
[GenerateSerializer, Alias("platform.parts.not-holder")]
internal sealed class NotHolderException(string message) : Exception(message);
