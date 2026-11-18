using System.Text.Json;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps;

// The pasted links Ino keeps in its records neuron ({brain}/apps/ino/records), as the chat's link store reads them
// (ChatAppCard.FindLink).
public static class InoLinks
{
    public static async Task<string> Json(IServiceProvider services, string brain)
    {
        ArgumentNullException.ThrowIfNull(services);
        var records = await SiloNeurons.Get<IInoRecords>(services, $"{brain}/{IInoRecords.Key}").Read();
        return System.Text.Encoding.UTF8.GetString(DigitalBrain.Invocation.RuntimeProtocol.SerializeToUtf8Bytes(records.Links, typeof(InoLink[])));
    }
}
