using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Sdk.Modules;

// Decides whether a module that declares a LicenseProduct may run. The check is cooperative: it runs in the
// open composition root and gates start-up only. Distribution of the paid packages is what enforces the licence.
public interface ILicenseCheck
{
    LicenseResult Check(ModuleId module, LicenseContext context);
}

public sealed record LicenseContext(IConfiguration Configuration, string Product);

// Reason is one of "missing", "invalid" or "expired" when Valid is false; never the token itself.
public sealed record LicenseResult(bool Valid, string? Holder, DateTimeOffset? Expires, string? Reason);
