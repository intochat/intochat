using System.Text.Json;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

// The pasted links Ino keeps in its records neuron ({brain}/apps/ino/records), as Ino's link store reads them
// (InoLinkCard.FindLink).
public static class InoLinks
{
    public static async Task<string> Json(IServiceProvider services, string brain)
    {
        ArgumentNullException.ThrowIfNull(services);
        var brainId = brain is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brain);
        var records = await SiloNeurons.Get<IInoRecords>(services, new DigitalBrain.NeuronId(brainId, "apps.ino-records", IInoRecords.Key).FullKey()).Read();
        return System.Text.Encoding.UTF8.GetString(DigitalBrain.Invocation.RuntimeProtocol.SerializeToUtf8Bytes(records.Links, typeof(InoLink[])));
    }
}
