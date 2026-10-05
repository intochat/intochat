namespace DigitalBrain.Apps;

// A named connection slot in shared source. The installer supplies its own connector id.
[GenerateSerializer, Alias("apps.package-account")]
public sealed record PackageConnection(
    [property: Id(0)] string Name,
    [property: Id(1)] string Source,
    [property: Id(2)] string Description);
