namespace DigitalBrain;

public interface IIntentUsageEntry;

public sealed record IntentUsageBatch(string IntentId, string? ScopeId, IIntentUsageEntry[] Usage);
