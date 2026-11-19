using IntoChat.OS.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
builder.AddIntoChatOSPipelines();

// The deployment: IntoChatOS:Topology picks models and storage, module enablement picks hosting (DeploymentProfile).
var profile = DeploymentProfile.From(builder);
var os = builder.AddIntoChatOS("Modules", profile, IntoChatOSHostingOptions.Named(builder.Configuration, "os"));
os.AddKernel("../IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj", profile);

builder.Build().Run();
