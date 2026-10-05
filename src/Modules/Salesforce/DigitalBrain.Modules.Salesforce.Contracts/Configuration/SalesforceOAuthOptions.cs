namespace DigitalBrain.Salesforce;

// Contains credentials. Do not serialize or log this options instance.
public sealed class SalesforceOAuthOptions
{
    public static readonly string SectionName = DigitalBrain.DigitalBrainConfiguration.ModuleSection(new DigitalBrain.ModuleId("salesforce")) + ":OAuth";
    public string ConsumerKey { get; set; } = "";
    public string ConsumerSecret { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
}
