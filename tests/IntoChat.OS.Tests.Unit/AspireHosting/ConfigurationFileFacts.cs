using IntoChat.OS.Kernel.AspireServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ConfigurationFileFacts
{
    [Fact]
    public void ConfigurationFileIsLoadedWhenTheKeyIsSet()
    {
        using var home = new DigitalBrain.Testing.TempDirectory("intochat-config-");
        var path = Path.Combine(home.Path, "config.json");
        File.WriteAllText(path, """{ "IntoChatOS": { "Probe": "from-file" } }""");

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IntoChatOSNames.HomeKey] = home.Path,
            [ServerHostingExtensions.ConfigurationFileKey] = path,
        });
        builder.AddIntoChatOSServer(server => { });
        Assert.Equal("from-file", builder.Configuration["IntoChatOS:Probe"]);
        Assert.Equal("IntoChatOS__ConfigurationFile", ServerHostingExtensions.ConfigurationFileEnvironmentVariable);
    }
}
