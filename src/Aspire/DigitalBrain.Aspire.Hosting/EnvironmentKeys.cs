namespace DigitalBrain.Aspire.Hosting;

// "DigitalBrain:Integrations:gmail:ClientId" -> DigitalBrain__Integrations__gmail__ClientId,
// the same key the module reads back through IConfiguration. Every projection converts here.
public static class EnvironmentKeys
{
    public static string For(string configurationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        return configurationKey.Replace(":", "__", StringComparison.Ordinal);
    }

    public static string For(string configurationRoot, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return For($"{configurationRoot}:{name}");
    }
}
