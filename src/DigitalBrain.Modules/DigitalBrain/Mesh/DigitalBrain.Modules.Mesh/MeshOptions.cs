using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Mesh;

// Wide coding because home nodes sleep: any DataShards of DataShards+ParityShards rebuild an object.
public sealed class MeshOptions : IModuleOptions
{
    public int DataShards { get; set; } = 8;

    public int ParityShards { get; set; } = 16;

    public int ChallengesPerShard { get; set; } = 16;

    public void Validate()
    {
        if (DataShards < 1 || ParityShards < 1 || DataShards + ParityShards > 255)
        { throw new InvalidOperationException("Mesh needs DataShards >= 1, ParityShards >= 1 and at most 255 shards in total."); }
        if (ChallengesPerShard < 1) { throw new InvalidOperationException("Mesh needs at least one storage challenge per shard."); }
    }
}
