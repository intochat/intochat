namespace DigitalBrain.Framework;

public sealed class DocumentConflictException() : InvalidOperationException("The document changed repeatedly; retry the operation.");
