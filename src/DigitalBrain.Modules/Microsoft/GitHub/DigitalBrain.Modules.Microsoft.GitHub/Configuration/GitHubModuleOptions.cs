using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.GitHub;

// Public deployment metadata only. The App id, key, OAuth client and webhook secret are the
// github Integration's; repository connections are per-brain neurons.
public sealed class GitHubModuleOptions : IModuleOptions
{
    public static readonly string SectionName = DigitalBrainConfiguration.ModuleOptionsSection(new ModuleId("github"));

    public string PublicWebhookUrl { get; set; } = "";

    public void Validate()
    {
        if (PublicWebhookUrl.Length != 0 && (!Uri.TryCreate(PublicWebhookUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"))
        { throw new ArgumentException("The GitHub webhook URL must be an absolute HTTPS URL."); }
    }
}
