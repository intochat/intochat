namespace DigitalBrain.Testing.Aspire;

public static class AspireBrainExtensions
{
    // The connection string of one of the AppHost's resources, e.g. a fixture database.
    public static ValueTask<string?> GetConnectionStringAsync(this ITestBrain brain, string resourceName, CancellationToken cancellationToken = default)
        => Of(brain).Session.GetConnectionStringAsync(resourceName, cancellationToken);

    internal static AspireBrain Of(ITestBrain brain)
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
