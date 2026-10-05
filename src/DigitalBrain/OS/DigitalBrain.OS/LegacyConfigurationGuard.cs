using Microsoft.Extensions.Configuration;

namespace DigitalBrain.OS;

// The pre-grammar roots. Configuration still carrying one is a deployment bug the host
// refuses at startup; silence would revive the old key as dead weight.
public static class LegacyConfigurationGuard
{
    private static readonly (string Legacy, string Target)[] RetiredSections =
    [
        ("DigitalBrain:AI", "DigitalBrain:Modules:ai:Options"),
        ("DigitalBrain:Workspace", "DigitalBrain:Modules:ai:Options:Workspace"),
        ("DigitalBrain:Apps", "DigitalBrain:Modules:apps:Options"),
        ("DigitalBrain:ShippedApps", "DigitalBrain:Modules:apps:Shipped"),
        ("DigitalBrain:CSharp", "DigitalBrain:Modules:csharp:Deployment"),
        ("DigitalBrain:Microsoft:GitHub", "DigitalBrain:Modules:github"),
        ("DigitalBrain:Microsoft:Aspire", "DigitalBrain:Modules:aspire"),
        ("DigitalBrain:Salesforce:OAuth", "DigitalBrain:Modules:salesforce:OAuth"),
        ("DigitalBrain:Flutter:Hosting", "DigitalBrain:Modules:flutter:Hosting"),
        ("DigitalBrain:Time", "DigitalBrain:Modules:time:Options"),
    ];

    public static void Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var offenses = RetiredSections
            .Where(section => configuration.GetSection(section.Legacy).Exists())
            .Select(section => $"'{section.Legacy}' moved to '{section.Target}'")
            .ToArray();
        if (offenses.Length > 0)
        { throw new InvalidOperationException("Retired configuration sections — migrate the keys: " + string.Join("; ", offenses)); }
    }
}
