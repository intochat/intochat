using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ConfigurationGrammarFacts
{
    [Fact]
    public void TheEnvironmentFormOfAKeySwapsEverySectionSeparatorForADoubleUnderscore()
        => Assert.Equal("A__B__C", IntoChatOSConfiguration.EnvironmentName("A:B:C"));
}
