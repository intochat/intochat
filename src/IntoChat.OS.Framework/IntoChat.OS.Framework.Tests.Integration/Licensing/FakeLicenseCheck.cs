using System.Collections.Concurrent;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.Integration.Licensing;

public sealed class FakeLicenseCheck(Func<ModuleId, LicenseResult> verdict) : ILicenseCheck
{
    public ConcurrentQueue<(ModuleId Module, string Product)> Calls { get; } = new();

    public LicenseResult Check(ModuleId module, LicenseContext context)
    {
        Calls.Enqueue((module, context.Product));
        return verdict(module);
    }

    public static FakeLicenseCheck Licensed { get; } = new(_ => new LicenseResult(true, "holder", null, null));

    public static FakeLicenseCheck Unlicensed(string reason) => new(_ => new LicenseResult(false, null, null, reason));
}
