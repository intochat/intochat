namespace IntoChat.OS.Modules.AI.Agents;

public enum AgentFailure
{
    Unknown = 0,
    NoProvider = 1,
    ProviderRejectedKey = 2,
    ProviderUnavailable = 3,
    ToolFailed = 4,
    TimedOut = 5,
}
