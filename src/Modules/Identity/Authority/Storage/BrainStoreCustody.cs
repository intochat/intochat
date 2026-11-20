using DigitalBrain.Identity.Local.Custody;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Storage;

// The brain store's custody rules, checked when the silo starts (the kernel composition runs them).
public static class BrainStoreCustody
{
    public static void Validate(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetRequiredService<IOptions<OsBrainStoreOptions>>().Value;
        var configured = services.GetRequiredService<IMasterKeyCustody>() is DigitalBrain.Identity.Local.Custody.ConfiguredMasterKeyCustody;
        if (options.Custody == MasterKeyCustodyKind.Tpm && configured)
        { throw new InvalidOperationException("TPM custody requires the Windows connector and an available TPM."); }
        if (services.GetRequiredService<IHostEnvironment>().IsProduction() && configured && !options.ConfiguredCustodyAcknowledged)
        { throw new InvalidOperationException("Production storage requires TPM custody or explicit ConfiguredCustodyAcknowledged."); }
    }
}
