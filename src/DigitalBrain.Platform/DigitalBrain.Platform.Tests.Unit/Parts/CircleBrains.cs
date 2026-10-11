using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// A host with the Apps and Circles modules over the repository's features folder, and circles created the way a
// circle is born (T26-1): the trust side records a brain of kind Circle for the creator, and BrainBirths gives it life.
internal static class CircleBrains
{
    private const string SolutionFile = "DigitalBrain.slnx";

    public static readonly string Features = Path.Combine(FindRoot(), "features");

    public static Task<ITestBrain> Start(CancellationToken ct, Func<TestBrain, TestBrain>? configure = null)
    {
        var builder = TestBrain.Create().WithModule<AppsModule, AppsOptions>(options => options.Features = Features).WithModule<CirclesModule>()
            // No csharp module here: the programs are data, deployed nowhere.
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime, NoRuntime>();
            });
        return (configure?.Invoke(builder) ?? builder).StartAsync(ct);
    }

    public static async Task<string> Create(ITestBrain brain, Person creator, string name = "friends")
    {
        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        var provision = await identity.CreateBrain(creator.AccountId, creator.PrincipalId, Guid.NewGuid().ToString("N"), name, BrainKind.Circle);
        await brain.Services.GetRequiredService<BrainBirths>().Born(provision);
        return provision.Member.BrainId;
    }

    // The person as the /brains/{circle} route stamps it: their own account, inside the circle brain (T26-8).
    public static async Task<T> In<T>(this Person person, string brainId, Func<Task<T>> call)
    {
        using (Enforcement.CallerContextScope.Scoped(Stamp(person, brainId))) { return await call(); }
    }

    public static async Task In(this Person person, string brainId, Func<Task> call)
    {
        using (Enforcement.CallerContextScope.Scoped(Stamp(person, brainId))) { await call(); }
    }

    public static ICircle Circle(ITestBrain brain, string circle) => brain.Get<ICircle>(ICircle.Key(circle));

    public static async Task Join(ITestBrain brain, string circle, Person creator, Person member)
    {
        await creator.In(circle, () => Circle(brain, circle).Invite(member.AccountId));
        await member.In(circle, () => Circle(brain, circle).Accept());
    }

    // The app names a person sees at /brains/{brain}/apps, or the status code that refused them.
    public static async Task<(int Status, string[] Apps)> AppsOf(Person person, string brainId, CancellationToken ct)
    {
        using var response = await person.Http.GetAsync($"/brains/{brainId}/apps", ct);
        if (!response.IsSuccessStatusCode) { return ((int)response.StatusCode, []); }
        var items = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return ((int)response.StatusCode, [.. items.EnumerateArray().Select(item => item.GetProperty("name").GetString()!)]);
    }

    private static CallerContext Stamp(Person person, string brainId) => new()
    {
        PrincipalId = person.PrincipalId, AccountId = person.AccountId, BrainId = brainId,
        Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp,
    };

    private sealed class NoRuntime : IAppRuntime
    {
        public Task Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFile))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException($"{SolutionFile} was not found above {AppContext.BaseDirectory}.");
    }
}
