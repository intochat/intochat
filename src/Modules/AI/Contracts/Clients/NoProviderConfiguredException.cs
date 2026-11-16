namespace IntoChat.OS.Modules.AI;

public sealed class NoProviderConfiguredException : InvalidOperationException
{
    public NoProviderConfiguredException()
        : base("No AI provider is configured.") { }

    public NoProviderConfiguredException(string message)
        : base(message) { }

    public NoProviderConfiguredException(string message, Exception innerException)
        : base(message, innerException) { }
}
