using IntoChat.OS.AppHost;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.ServiceDefaults;

var builder = DistributedApplication.CreateBuilder(args);
// Legacy product-name settings stop the app host outside Development (the app host has no AddServiceDefaults).
LegacyConfigurationGuard.Verify(builder.Configuration, builder.Environment);
switch (args)
{
    case ["reset", .. var flags]: return await LocalReset.RunFromAppHostAsync(builder, flags);
    case ["package-msix", .. var options]: return await MsixPackager.RunAsync(options);
}

// The deployment: IntoChatOS:Topology picks models and storage, module enablement picks hosting (DeploymentProfile).
var profile = DeploymentProfile.From(builder);
var os = builder.AddIntoChatOS("Modules", profile, IntoChatOSHostingOptions.Named(builder.Configuration, "os"));
os.AddKernel<Projects.IntoChat_OS_Kernel>(profile);

builder.Build().Run();
return 0;
