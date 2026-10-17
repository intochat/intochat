namespace DigitalBrain.Equifax;

// The message is the provider status (e.g. "unauthorized (401)"), never a credential.
[GenerateSerializer, Alias("equifax.unavailable")]
public sealed class EquifaxUnavailableException(string message) : InvalidOperationException(message);
