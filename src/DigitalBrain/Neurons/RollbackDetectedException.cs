namespace DigitalBrain;

// A refusal word: the store served an older version of a part than this machine already saw (Q10).
[GenerateSerializer, Alias("framework.rollback-detected")]
public sealed class RollbackDetectedException(string message) : Exception(message);
