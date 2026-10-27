using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ConfigurationGrammarFacts
{
    [Fact]
    public void TheEnvironmentFormOfAKeySwapsEverySectionSeparatorForADoubleUnderscore()
        => Assert.Equal("A__B__C", IntoChatOSConfiguration.EnvironmentName("A:B:C"));
}
