using IntoChat.OS.Aspire.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ConfigurationFileFacts
{
    [Fact]
    public void ConfigurationFileIsLoadedWhenTheKeyIsSet()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, """{ "DigitalBrain": { "Probe": "from-file" } }""");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ServerHostingExtensions.ConfigurationFileKey] = path,
            });
            builder.AddIntoChatOSServer(server => { });
            Assert.Equal("from-file", builder.Configuration["DigitalBrain:Probe"]);
            Assert.Equal("DigitalBrain__ConfigurationFile", ServerHostingExtensions.ConfigurationFileEnvironmentVariable);
        }
        finally { File.Delete(path); }
    }
}
