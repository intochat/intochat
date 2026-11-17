using IntoChat.Cloud;
using IntoChat.Cloud.Lobby;
using IntoChat.OS.Kernel;

Environment.ExitCode = await KernelHost.RunAsync(
    args,
    typeof(CloudHost).Assembly,
    configureServer: static (server, _) => server
        .UseLicenseCheck(new CloudLicenseCheck())
        .ConfigureServices(CloudHost.Register),
    beforeAccountSession: static web =>
    {
        if (web.Services.GetService<GuestTrySessionService>() is not null)
        {
            web.UseGuestTrySessions();
        }
    });
