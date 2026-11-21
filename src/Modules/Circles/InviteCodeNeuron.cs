using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Circles;

internal sealed class CircleInviteRedeemer(IServiceProvider plain) : ICircleInviteRedeemer
{
    public async Task<RedeemOutcome> Redeem(string code, string machineId, string machineKeyThumbprint, string machineName)
    {
        if (!InviteCode.TryNormalize(code, out var normalized)) { return RedeemOutcome.Rejected; }
        var edge = plain.GetService<EdgeAuthority>();
        using (edge?.Enter(edge.Module("circles", new DigitalBrain.BrainId("system"))))
        {
            var hash = InviteCode.Hash(normalized, await SiloNeurons.Get<ICodeKey>(plain, ICodeKey.Key).Get());
            var target = await SiloNeurons.Get<ICodeIndex>(plain, ICodeIndex.Key(hash)).Resolve(CodePurpose.Device);
            if (target is null) { return RedeemOutcome.Rejected; }
            return await SiloNeurons.Get<ICircleInvites>(plain, ICircleInvites.Key(target.Brain.Value))
                .Redeem(target.Reference, new RedeemInvite(hash, machineId, machineKeyThumbprint, machineName));
        }
    }
}
