using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace IntoChat.OS.Testing.Aspire;

public static class AspireBrainExtensions
{
    // The connection string of one of the AppHost's resources, e.g. a fixture database.
    public static ValueTask<string?> GetConnectionStringAsync(this ITestBrain brain, string resourceName, CancellationToken cancellationToken = default)
        => Of(brain).Session.GetConnectionStringAsync(resourceName, cancellationToken);

    // A client for another of the AppHost's resources (a consuming process beside the brain), once it is healthy.
    public static async Task<HttpClient> CreateHttpClientAsync(this ITestBrain brain, string resourceName, string endpointName, CancellationToken cancellationToken = default)
    {
        var app = Of(brain).Session.App;
        await app.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, cancellationToken).ConfigureAwait(false);
        return app.CreateHttpClient(resourceName, endpointName);
    }

    /// <summary>The Aspire brain behind <paramref name="brain"/>; throws when the brain is not on an Aspire target.</summary>
    public static AspireBrain Of(ITestBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain as AspireBrain
            ?? throw new NotSupportedException("Only a brain on an Aspire target has AppHost resources; start it with On(new AspireTarget<TAppHost>()).");
    }
}

// One booted AppHost for every fact of a collection; each fact leases its own view of it.
public abstract class SharedAspireBrain : SharedBrain<ITestBrain>
{
    protected sealed override ITestBrain Lease(ITestBrain host) => AspireBrainExtensions.Of(host).Lease();
}
