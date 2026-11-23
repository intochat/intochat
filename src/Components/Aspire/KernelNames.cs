namespace IntoChat.Aspire;

// The kernel's resource names as the aspire module meets them. A local run names the one kernel instance "os"
// (front "os-gateway"), several instances "os-a", "os-b"; a published or older deployment names it "OperatingSystem-a" (front "OperatingSystem-front").
// The AppHost cannot be referenced from here: IntoChat.OS.Aspire.Hosting.ResourceNames states the same rule for the
// AppHost, and a test on each side pins the same strings. Stored telemetry carries either name.
internal static class KernelNames
{
    public const string Os = "os";
    public const string Legacy = "OperatingSystem";

    public static bool IsKernel(string? name)
        => string.Equals(name, Os, StringComparison.OrdinalIgnoreCase) || string.Equals(name, Legacy, StringComparison.OrdinalIgnoreCase);

    public static bool Same(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase) || (IsKernel(left) && IsKernel(right));

    // A role runs {role}-{letter} instances; the "os" role names a single instance after the role itself.
    public static bool IsInstance(string? name, string role)
        => name is not null && ((role == Os && name == Os)
            || (name.Length == role.Length + 2 && name.StartsWith(role + "-", StringComparison.Ordinal) && char.IsAsciiLetterLower(name[^1])));

    // Is this live resource the kernel or part of it (its front, a replica, its rebuilder)? A refusal rule, so it fails closed and
    // depends on no suffix format: the exact name, the display name list_resources gives, or the display name AspireMcp.DisplayNameOf
    // derives is, or begins with followed by '-', '_' or '.', a kernel base ("os", "OperatingSystem", or this process's own instance
    // name when it has one), ignoring case. So os-gateway, os-rebuilder or a hypothetical os-db cannot be restarted through IAspire;
    // a name that only begins with the letters ("oscar", "OperatingSystems") can. Every rebuilder shape begins with a base, so it needs no rule of its own.
    public static bool IsSelfLive(string liveName, string? displayField, string? self)
        => new[] { liveName, displayField, AspireMcp.DisplayNameOf(liveName), displayField is null ? null : AspireMcp.DisplayNameOf(displayField) }
            .OfType<string>()
            .Any(name => BeginsWith(name, Os) || BeginsWith(name, Legacy) || (!string.IsNullOrEmpty(self) && BeginsWith(name, self)));

    private static bool BeginsWith(string name, string kernelBase)
        => name.StartsWith(kernelBase, StringComparison.OrdinalIgnoreCase)
            && (name.Length == kernelBase.Length || name[kernelBase.Length] is '-' or '_' or '.');

    public const string Front = "os-gateway";
    public const string LegacyFront = "OperatingSystem-front";

    public static string RoleOf(string instance) => instance == Os ? Os : instance[..^2];
}
