using IntoChat.OS.Sdk;

namespace IntoChat.OS.Modules.Google.Gmail;

// The ModuleHosting<GmailModule> link is generated; secret projection is the base default.
[ModuleHosting(typeof(GmailModule))]
public sealed partial class GmailModuleHosting;
