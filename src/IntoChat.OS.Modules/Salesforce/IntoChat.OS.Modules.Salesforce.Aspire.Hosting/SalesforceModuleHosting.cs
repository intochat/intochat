using IntoChat.OS.Sdk;

namespace IntoChat.OS.Modules.Salesforce.Aspire.Hosting;

// The ModuleHosting<SalesforceModule> link is generated; secret projection is the base default.
[ModuleHosting(typeof(IntoChat.OS.Modules.Salesforce.SalesforceModule))]
public sealed partial class SalesforceModuleHosting;
