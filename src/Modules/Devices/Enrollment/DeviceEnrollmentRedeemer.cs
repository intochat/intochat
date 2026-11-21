using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Devices;

internal sealed class DeviceEnrollmentRedeemer : IDeviceEnrollmentRedeemer
{
    private readonly IServiceProvider _services;
    private readonly ICodeBook _book;
    private readonly Func<IServiceProvider, string, IDeviceEnrollment> _enrollment;

    public DeviceEnrollmentRedeemer(IServiceProvider services, ICodeBook book)
        : this(services, book, static (sp, brain) => SiloNeurons.Get<IDeviceEnrollment>(sp, IDeviceEnrollment.Key(brain)))
    {
    }

    internal DeviceEnrollmentRedeemer(
        IServiceProvider services,
        ICodeBook book,
        Func<IServiceProvider, string, IDeviceEnrollment> enrollment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(enrollment);
        _services = services;
        _book = book;
        _enrollment = enrollment;
    }

    public async Task<EnrollmentOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName)
    {
        if (!Code.TryParse(code, out var normalized))
        {
            return EnrollmentOutcome.Rejected;
        }

        var hash = await _book.HashOf(normalized).ConfigureAwait(false);
        var target = await _book.Lookup(CodePurpose.Device, hash).ConfigureAwait(false);
        if (target is null)
        {
            return EnrollmentOutcome.Rejected;
        }

        EnrollmentOutcome outcome;
        var edge = _services.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module(DevicesModule.ManifestName, target.Brain)))
        {
            outcome = await _enrollment(_services, target.Brain.Value)
                .Redeem(hash, machineId, machineKeyThumbprint, machineName)
                .ConfigureAwait(false);
        }

        if (outcome.Result == EnrollmentResult.Accepted)
        {
            await _book.Spend(hash).ConfigureAwait(false);
        }

        return outcome;
    }
}
