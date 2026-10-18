namespace IntoChat.OS.Framework;

// Ledger 38: the kernel instance directory is a durable catalog (a deactivated app/file stays listed until
// deleted), so a brain's core can grow with every neuron ever written. Compaction keeps each "{brain}/{type}"
// directory at or below Limit: when a registration exceeds it, the oldest entries that are not currently
// active are dropped, so ephemeral neurons (answer texts, recalls) do not accumulate without bound while
// active instances and small catalogued types (apps, files) are untouched.
public sealed class InstanceDirectoryOptions
{
    public const string Key = "DigitalBrain:Kernel:InstanceDirectory";

    public int Limit { get; set; } = 4096;
}
