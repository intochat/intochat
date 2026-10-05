namespace DigitalBrain.Salesforce;

// Contains credentials. Do not serialize or log this options instance.
public sealed class SalesforceOAuthOptions
{
    public static readonly string SectionName = DigitalBrain.Contracts.DigitalBrainConfiguration.ModuleSection(new DigitalBrain.Contracts.ModuleId("salesforce")) + ":OAuth";
    public string ConsumerKey { get; set; } = "";
    public string ConsumerSecret { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
}
