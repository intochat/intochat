namespace IntoChat.OS.Kernel.Tests.E2E;

internal static class ProductE2E
{
    // The product host IS the reference boot; what is the product's own is its cookie and the
    // shipped content a product fixture turns back on.
    public static TestBrain Create(string modelApiKey = "fixture-key", Dictionary<string, string?>? privateConfiguration = null)
        => ReferenceBrain.Create(modelApiKey, privateConfiguration)
            .WithHostConfiguration(IntoChat.OS.Connections.Identity.Configuration.IdentityHostOptions.Key(nameof(IntoChat.OS.Connections.Identity.Configuration.IdentityHostOptions.CookieName)), "intochat.session")
            .WithHostConfiguration(IntoChat.OS.Connections.Identity.Configuration.IdentityHostOptions.Key(nameof(IntoChat.OS.Connections.Identity.Configuration.IdentityHostOptions.ProtectionApplicationName)), "IntoChat.v1")
            .WithHostConfiguration(IntoChat.OS.Connections.Identity.Configuration.IdentityHostOptions.Key(nameof(IntoChat.OS.Connections.Identity.Configuration.IdentityHostOptions.ProtectionContainerName)), "intochat-protection-v1");
}
