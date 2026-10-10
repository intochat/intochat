using System.Security.Cryptography;
using DigitalBrain.Mesh;

namespace DigitalBrain.Modules.Mesh.Tests;

public sealed class ReedSolomonFacts
{
    [Theory]
    [InlineData(4, 2, 1000)]
    [InlineData(8, 16, 4097)]
    [InlineData(10, 4, 1)]
    public void AnyKOfNShardsRebuildTheData(int k, int m, int size)
    {
        var rs = new ReedSolomon(k, m);
        var data = RandomNumberGenerator.GetBytes(size);
        var shards = rs.Encode(data, out var shardSize);

        foreach (var lost in Combinations(k + m, m).Take(200))
        {
            var partial = shards.Select((s, i) => lost.Contains(i) ? null : (byte[]?)s.ToArray()).ToArray();
            var rebuilt = rs.Reconstruct(partial, shardSize);
            Assert.Equal(data, rs.Join(rebuilt, size));
            for (var i = 0; i < k + m; i++) { Assert.Equal(shards[i], rebuilt[i]); }
        }
    }

    [Fact]
    public void LosingMoreThanMShardsIsUnrecoverable()
    {
        var rs = new ReedSolomon(4, 2);
        var shards = rs.Encode(new byte[100], out var size);
        var partial = shards.Select((s, i) => i < 3 ? null : (byte[]?)s).ToArray();
        Assert.Throws<InvalidOperationException>(() => rs.Reconstruct(partial, size));
    }

    private static IEnumerable<int[]> Combinations(int n, int r)
    {
        var idx = Enumerable.Range(0, r).ToArray();
        while (true)
        {
            yield return idx.ToArray();
            var i = r - 1;
            while (i >= 0 && idx[i] == n - r + i) { i--; }
            if (i < 0) { yield break; }
            idx[i]++;
            for (var j = i + 1; j < r; j++) { idx[j] = idx[j - 1] + 1; }
        }
    }
}
