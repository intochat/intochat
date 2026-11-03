// Rewrites IntoChat.OS framework/contracts/connections usings and qualified references in os/src
// to their DigitalBrain equivalents (Task 23).
using System.Text;
using System.Text.RegularExpressions;

var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
var srcRoot = Path.Combine(repoRoot, "src");

var dirtyAppsFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS.Modules/IntoChat.OS/Apps/src/IntoChat.OS.Modules.Apps/Brain/AppGraphSource.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS.Modules/IntoChat.OS/Apps/src/IntoChat.OS.Modules.Apps/Brain/BrainSceneGraph.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS.Modules/IntoChat.OS/Apps/src/IntoChat.OS.Modules.Apps/Brain/BrainSceneHost.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS.Modules/IntoChat.OS/Apps/src/IntoChat.OS.Modules.Apps/Hosting/CompiledAppsHost.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS.Modules/IntoChat.OS/Apps/tests/IntoChat.OS.Modules.Apps.Tests.Unit/BrainSceneGraphFacts.cs")),
};

// Skip files in IntoChat.OS and IntoChat.OS.Framework that define the moved types themselves (deleted in Task 24).
var deletedInTask24 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS/Hosting/IntoChat.OS.Framework/Modules/Configuration/IntoChatOSConfiguration.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS/Hosting/IntoChat.OS.Framework/Modules/Configuration/ModuleOptionsSerialization.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS/Hosting/IntoChat.OS.Framework/Callers/CallerContextStamper.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS/Hosting/IntoChat.OS.Framework/Callers/BrainKey.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS/Hosting/IntoChat.OS.Framework/Callers/BrainScope.cs")),
    Path.GetFullPath(Path.Combine(srcRoot, "IntoChat.OS/Hosting/IntoChat.OS.Framework/Modules/CallerAdmission.cs")),
};

var files = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj" or "Contracts.Compute" or "Contracts.Configuration"))
    .Where(path => !dirtyAppsFiles.Contains(Path.GetFullPath(path)))
    .Where(path => !deletedInTask24.Contains(Path.GetFullPath(path)))
    .Order(StringComparer.Ordinal)
    .ToArray();

var (tokenRules, inlineReplacements) = BuildRules();
var modified = 0;

foreach (var file in files)
{
    var original = File.ReadAllText(file);
    var isSystemOneHostingState = Path.GetFileName(file).Equals("SystemOneHostingState.cs", StringComparison.OrdinalIgnoreCase);
    var updated = RewriteFile(original, tokenRules, inlineReplacements, isSystemOneHostingState);
    if (!string.Equals(original, updated, StringComparison.Ordinal))
    {
        File.WriteAllText(file, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        modified++;
    }
}

Console.WriteLine($"Processed {files.Length} files; updated {modified} files.");

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static (List<(string Namespace, Regex Pattern)> TokenRules, List<(string OldText, string NewText)> InlineReplacements) BuildRules()
{
    var inlineReplacements = new List<(string OldText, string NewText)>
    {
        ("IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration", "DigitalBrain.Modules.Configuration.ModuleConfiguration"),
        ("public const string SectionName = DigitalBrain.Modules.Configuration.ModuleConfiguration.ConnectionsSectionName;", "public static string SectionName => DigitalBrain.Modules.Configuration.ModuleConfiguration.ConnectionsSectionName;"),
        ("public const string KeyConfiguration = DigitalBrain.Modules.Configuration.ModuleConfiguration.DiagnosticsKey;", "public static string KeyConfiguration => DigitalBrain.Modules.Configuration.ModuleConfiguration.DiagnosticsKey;"),
        ("IntoChatOSConfiguration.", "ModuleConfiguration."),
        ("IntoChat.OS.Framework.Modules.Configuration.ModuleOptionsSerialization", "DigitalBrain.Modules.Options.ModuleOptionsSerialization"),
        ("IntoChat.OS.Contracts.Configuration.ConfigurationKeyGrammar", "DigitalBrain.Modules.Configuration.ConfigurationKeyGrammar"),
        ("IntoChat.OS.Framework.Modules.IModuleOptions", "DigitalBrain.Modules.Options.IModuleOptions"),
        ("IntoChat.OS.Framework.Modules.ModuleId", "DigitalBrain.Modules.Options.ModuleId"),
        ("IntoChat.OS.Runtime.PlainNeuronContracts", "DigitalBrain.Contracts.PlainNeuronContracts"),
        ("IntoChat.OS.Runtime.ConnectionContract", "DigitalBrain.Contracts.ConnectionContract"),
        ("IntoChat.OS.Integrations.IConnectionRequests", "DigitalBrain.Capabilities.Connections.Requests.IConnectionRequests"),
        ("IntoChat.OS.Framework.Parts.IMasterKeyCustody", "DigitalBrain.Identity.Local.Custody.IMasterKeyCustody"),
        ("IntoChat.OS.Framework.Parts.MasterKeyCustodyKind", "DigitalBrain.Identity.Local.Custody.MasterKeyCustodyKind"),
        ("IntoChat.OS.Framework.Modules.IBrainAccess", "DigitalBrain.Runtime.Http.Surface.IBrainAccess"),
        ("IntoChat.OS.Framework.Modules.Http.IRequestThrottle", "DigitalBrain.Runtime.Http.Surface.IRequestThrottle"),
        ("IntoChat.OS.Framework.Modules.Http.SlidingWindowThrottle", "DigitalBrain.Runtime.Http.Surface.SlidingWindowThrottle"),
        ("IntoChat.OS.Framework.Modules.Metering.ModulePriceListRegistry", "DigitalBrain.Capabilities.Compute.ModulePriceListRegistry"),
        ("IntoChat.OS.Framework.BrainKey", "DigitalBrain.Brains.BrainKey"),
        ("IntoChat.OS.Framework.CallerContextStamper", "DigitalBrain.Runtime.Orleans.Capabilities.CallerContextStamper"),
    };

    var tokenRules = new List<(string Namespace, Regex Pattern)>
    {
        ("DigitalBrain.Brains", WordRegex("BrainScope", "BrainKey", "CallerAdmission", "IBrain", "BrainState")),
        ("DigitalBrain.Contracts", WordRegex("PlainNeuronContracts", "ConnectionContract", "ConnectionContractAttribute")),
        ("DigitalBrain.Errors", WordRegex("ServiceUnavailableException", "StaleActionException")),
        ("DigitalBrain.Log", WordRegex("LogSwallowed", "Swallow")),
        ("DigitalBrain.Modules.Configuration", WordRegex("ModuleConfiguration", "ConfigurationKeyGrammar")),
        ("DigitalBrain.Modules.Options", WordRegex("IModuleOptions", "SettingAttribute", "GetModuleOptions", "ModuleOptionsSerialization", "ModuleId", "ModuleSettingsValidation")),
        ("DigitalBrain.Runtime.Http.Surface", WordRegex("IHttpModule", "IHttpSurface", "IBrainAccess", "BrainRoutes", "HttpProblems", "IpThrottleFilter", "IpThrottleOptions", "IRequestThrottle", "SlidingWindowThrottle", "ThrottleResult", "ForwardedHeadersSetup", "ForwardedHeadersHosting", "ReturnUrlValidator")),
        ("DigitalBrain.Runtime.Orleans.Capabilities", WordRegex("CallerContextStamper", "RuntimeCallerContext", "UntrustedCallerException")),
        ("DigitalBrain.Capabilities.Compute", WordRegex("ICostMeter", "IPriceList", "CostKind", "CostSummary", "CostRow", "Funding", "FundingKind", "CostSubject", "PriceQuote", "PricingStatus", "ExternalOperationDescriptor", "ExternalCallRequest", "ExternalCallHandle", "ExternalCallOutcome", "ExternalCallOutcomeKind", "ExternalCallCompletion", "ExternalCallContext", "ExternalCallMetadata", "ExternalCallHttp", "ExternalCallHandler", "ExternalCallState", "ExternalAccountingStatus", "ExternalChargePolicy", "IExternalCallContextAccessor", "ISingleAttemptHttpTransport", "BudgetExhaustedException", "ComputeTracePage", "TraceContext", "TraceDetailStatus", "UsageGrouping", "AddMeteredHttpClient", "MeteredHttpClients", "MeteredHttpClientRegistration", "AddModulePriceList", "ModulePriceListRegistration", "ModulePriceListRegistry", "ModulePriceLists")),
        ("DigitalBrain.Capabilities.Connections", WordRegex("IConnection", "IConnectionInbound", "ConnectionView", "OAuthGrant", "Credential", "ConnectionStatus", "ConnectionProbeOutcome", "ConnectionProbeResult", "ConnectionKeys", "ConnectionState", "ConnectionNeuron", "ConnectionNeuronTypes", "IConnectionIndex", "IConnectionCatalogUpdates", "IConnectionWriteFaults", "IBrainOwnerDispatch", "ConnectionConnected", "ConnectionDisconnected", "ConnectionExpired", "ConnectionFailing", "ConnectionUpdated")),
        ("DigitalBrain.Capabilities.Connections.Connectors", WordRegex("ConnectorDefinition", "OAuthMethod", "OAuthExternalOperations", "OAuthTokenExchangeException", "OAuthTokenFailure", "IOAuthTokenExchange", "IntegrationSchema", "HostConfiguration")),
        ("DigitalBrain.Capabilities.Connections.Integrations", WordRegex("IModuleIntegrations", "IIntegrationHandle", "IIntegrationStartup", "IntegrationSnapshot", "IntegrationStatus", "ConfigureIntegration", "ReleasedIntegration", "ICapabilities", "ICapabilitySource", "IntegrationCapability", "IRuntimeCapabilitySource", "NeuronType", "NeuronTypeHit")),
        ("DigitalBrain.Capabilities.Connections.Requests", WordRegex("IConnectionRequests", "ConnectionRequest", "ConnectionRef", "ConnectionRequested", "ConnectionsChanged", "ConnectionSummary", "ConnectorKind", "ConnectorOffer")),
        ("DigitalBrain.Capabilities.Connections.Sealing", WordRegex("ISecretSealer", "IBrainSecretSealer", "BrainSeal")),
        ("DigitalBrain.Capabilities.Connections.Broker", WordRegex("ICapabilityConnections", "BrainConnection", "ICapabilityBindingInterceptor")),
        ("DigitalBrain.Identity.Local.Custody", WordRegex("IMasterKeyCustody", "MasterKeyCustodyKind", "FileMasterKeyCustody", "SegmentSeal")),
    };

    return (tokenRules, inlineReplacements);
}

static Regex WordRegex(params string[] words)
    => new(@"\b(?:" + string.Join('|', words.Select(Regex.Escape)) + @")\b", RegexOptions.Compiled);

static string RewriteFile(
    string content,
    List<(string Namespace, Regex Pattern)> tokenRules,
    List<(string OldText, string NewText)> inlineReplacements,
    bool isSystemOneHostingState)
{
    var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

    var hadMixedOsUsing = lines.Any(l => IsMixedOsUsing(l.Trim()));
    var hadRemovedOsUsing = false;
    var hadComputeUsing = false;
    var hadHttpUsing = false;

    for (var i = lines.Count - 1; i >= 0; i--)
    {
        var trimmed = lines[i].Trim();
        if (IsRemovedOsUsing(trimmed, out var isCompute, out var isHttp))
        {
            hadRemovedOsUsing = true;
            hadComputeUsing |= isCompute;
            hadHttpUsing |= isHttp;
            lines.RemoveAt(i);
        }
    }

    var body = string.Join('\n', lines);
    var changedInline = false;
    foreach (var (oldText, newText) in inlineReplacements)
    {
        if (isSystemOneHostingState && oldText == "IntoChatOSConfiguration.")
        {
            continue;
        }
        if (body.Contains(oldText, StringComparison.Ordinal))
        {
            body = body.Replace(oldText, newText, StringComparison.Ordinal);
            changedInline = true;
        }
    }

    if (!hadRemovedOsUsing && !hadMixedOsUsing && !changedInline)
    {
        return content;
    }

    lines = body.Split('\n').ToList();
    var existingUsings = new HashSet<string>(
        lines.Select(l => l.Trim()).Where(l => l.StartsWith("using ", StringComparison.Ordinal) && l.EndsWith(';')),
        StringComparer.Ordinal);

    var neededNamespaces = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var (ns, pattern) in tokenRules)
    {
        if (pattern.IsMatch(body))
        {
            neededNamespaces.Add(ns);
        }
    }

    if (hadComputeUsing && Regex.IsMatch(body, @"\b(?:Cost|Trace)\b"))
    {
        neededNamespaces.Add("DigitalBrain.Capabilities.Compute");
    }

    if (hadHttpUsing && Regex.IsMatch(body, @"\bIpPrefix\b"))
    {
        neededNamespaces.Add("DigitalBrain.Runtime.Http.Surface");
    }

    if (Regex.IsMatch(body, @"\[\s*Setting\s*\("))
    {
        neededNamespaces.Add("DigitalBrain.Modules.Options");
    }

    var newUsingLines = neededNamespaces
        .Select(ns => $"using {ns};")
        .Where(u => !existingUsings.Contains(u))
        .ToList();

    if (isSystemOneHostingState)
    {
        const string aliasLine = "using IntoChatOSConfiguration = DigitalBrain.Modules.Configuration.ModuleConfiguration;";
        if (!existingUsings.Contains(aliasLine))
        {
            newUsingLines.Add(aliasLine);
        }
    }

    if (!hadRemovedOsUsing && !changedInline && newUsingLines.Count == 0)
    {
        return content;
    }

    if (newUsingLines.Count > 0)
    {
        var insertIndex = 0;
        while (insertIndex < lines.Count && lines[insertIndex].TrimStart().StartsWith("//", StringComparison.Ordinal))
        {
            insertIndex++;
        }
        lines.InsertRange(insertIndex, newUsingLines);
    }

    return string.Join(newline, lines);
}

static bool IsMixedOsUsing(string trimmedLine)
    => trimmedLine is "using IntoChat.OS;"
        or "using IntoChat.OS.Runtime;"
        or "using IntoChat.OS.Contracts.Configuration;"
        or "using IntoChat.OS.Framework;"
        or "using IntoChat.OS.Framework.Modules;"
        or "using IntoChat.OS.Connections.Platform.Connections;"
        or "using IntoChat.OS.Connections.Platform.Integrations;";

static bool IsRemovedOsUsing(string trimmedLine, out bool isCompute, out bool isHttp)
{
    isCompute = trimmedLine is "using IntoChat.OS.Contracts.Compute;"
        or "using IntoChat.OS.Framework.Compute;"
        or "using IntoChat.OS.Framework.Modules.Metering;";
    isHttp = trimmedLine is "using IntoChat.OS.Framework.Modules.Http;";
    return isCompute
        || isHttp
        || trimmedLine is "using IntoChat.OS.Framework.Configuration;"
            or "using IntoChat.OS.Framework.Diagnostics;"
            or "using IntoChat.OS.Framework.Modules.Configuration;"
            or "using IntoChat.OS.Framework.Modules.Platform.Integrations;"
            or "using IntoChat.OS.Framework.Parts;"
            or "using IntoChat.OS.Integrations;"
            or "using IntoChat.OS.Connections.Platform.Connections.Signals;";
}
