using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ConfigurationFileFacts
{
    [Fact]
    public void ConfigurationFileIsLoadedWhenTheKeyIsSet()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, """{ "IntoChatOS": { "Probe": "from-file" } }""");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.HomeKey] = Path.GetTempPath(),
                [ServerHostingExtensions.ConfigurationFileKey] = path,
            });
            builder.AddIntoChatOSServer(server => { });
            Assert.Equal("from-file", builder.Configuration["IntoChatOS:Probe"]);
            Assert.Equal("IntoChatOS__ConfigurationFile", ServerHostingExtensions.ConfigurationFileEnvironmentVariable);
        }
        finally { File.Delete(path); }
    }
}
