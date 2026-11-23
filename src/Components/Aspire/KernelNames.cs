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

    // The kernel or its front, exactly as the live list names them: restarting either cuts the caller's own connection.
    // Also this process's own instance name whatever its shape, and the rebuilder Aspire adds beside either.
    public static bool IsSelf(string name, string? self = null)
        => IsKernelOrSelf(name, self)
            || (name.Length > RebuilderSuffix.Length && name.EndsWith(RebuilderSuffix, StringComparison.Ordinal)
                && IsKernelOrSelf(name[..^RebuilderSuffix.Length], self));

    private static bool IsKernelOrSelf(string name, string? self)
        => name is Os or Legacy or Front or LegacyFront || IsInstance(name, Os) || IsInstance(name, Legacy)
            || (!string.IsNullOrEmpty(self) && string.Equals(name, self, StringComparison.Ordinal));

    private const string RebuilderSuffix = "-rebuilder";

    public const string Front = "os-gateway";
    public const string LegacyFront = "OperatingSystem-front";

    public static string RoleOf(string instance) => instance == Os ? Os : instance[..^2];
}
