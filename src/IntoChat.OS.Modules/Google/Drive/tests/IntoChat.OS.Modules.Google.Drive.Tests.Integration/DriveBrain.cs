using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Modules.Google.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// A brain with google-auth and google-drive only (no Gmail) whose Drive is the loopback stub.
internal static class DriveBrain
{
    public static TestBrain Compose(TestBrain brain, DriveStub drive) => brain
        .WithModule<GoogleAuthModule, GoogleAuthModuleOptions>()
        .WithModule<DriveModule, DriveModuleOptions>(options => options.Endpoint = drive.Endpoint)
        .WithIntegration(GoogleAuthModule.Integration, "ClientId", "integration-client")
        .WithIntegration(GoogleAuthModule.Integration, "ClientSecret", "integration-secret")
        .WithConfiguration(IntoChatOSConfiguration.ConnectionsPublicOriginKey, new Uri(drive.Endpoint, "/").AbsoluteUri);

    public static Task<ITestBrain> StartAsync(DriveStub drive, CancellationToken ct) => Compose(TestBrain.Create(), drive).StartAsync(ct);

    public static IServiceProvider Plain(this ITestBrain brain) => brain.Services;
}
