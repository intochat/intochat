namespace DigitalBrain.Platform.Tests.E2E;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions);
    }
}
