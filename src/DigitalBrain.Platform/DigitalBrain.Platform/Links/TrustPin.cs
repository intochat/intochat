using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Links;

// The trust host this machine has met is a different one: never a silent switch (T20-3).
internal sealed class TrustPinMismatchException(string message) : Exception(message);

// T20-3: a machine pins the trust host by its id (MachineKeys.IdOf its identity key): the configured Pin, else the
// first one seen, kept in {MachineDirectory}/trust.pin. Production requires a configured pin.
internal sealed class TrustPin(IOptions<TrustOptions> trust, IOptions<PartsOptions> parts)
{
    public const string PinKey = TrustOptions.Key + ":" + nameof(TrustOptions.Pin);

    private readonly Lock _gate = new();

    public string PinFile => Path.Combine(parts.Value.MachineDirectory, "trust.pin");

    public void Check(string trustId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustId);
        if (trust.Value.Pin is { Length: > 0 } pinned)
        {
            if (!string.Equals(pinned, trustId, StringComparison.Ordinal))
            { throw new TrustPinMismatchException($"The trust host is {trustId}, not the pinned {pinned} ({PinKey})."); }
            return;
        }
        lock (_gate)
        {
            if (File.Exists(PinFile))
            {
                var met = File.ReadAllText(PinFile).Trim();
                if (!string.Equals(met, trustId, StringComparison.Ordinal))
                { throw new TrustPinMismatchException($"The trust host is {trustId}, not {met} this machine met first ({PinFile})."); }
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PinFile)!);
            var temp = PinFile + ".tmp";
            File.WriteAllText(temp, trustId);
            File.Move(temp, PinFile, overwrite: false);
        }
    }

    // Trust on first use is a dev convenience: a production machine (own or cloud) names its trust host.
    public static void RequireInProduction(string environmentName, TrustOptions trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        if (trust.Endpoint is { Length: > 0 } && string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(trust.Pin))
        { throw new InvalidOperationException($"A production machine reaches its trust host only by a configured pin: set {PinKey}."); }
    }
}
