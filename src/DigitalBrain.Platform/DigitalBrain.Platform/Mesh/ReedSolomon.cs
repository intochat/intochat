namespace DigitalBrain.Platform.Mesh;

// Systematic Reed–Solomon over GF(2^8) with a Cauchy parity matrix: any k of the k+m shards rebuild the data.
internal sealed class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) { x ^= 0x11D; } // primitive polynomial x^8+x^4+x^3+x^2+1
        }
        for (int i = 255; i < 512; i++) { Exp[i] = Exp[i - 255]; }
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Inv(byte a) => a == 0 ? throw new DivideByZeroException() : Exp[255 - Log[a]];

    private readonly byte[][] _matrix; // n x k encoding matrix: identity on top, Cauchy below

    public int DataShards { get; }
    public int ParityShards { get; }
    public int TotalShards => DataShards + ParityShards;

    public ReedSolomon(int dataShards, int parityShards)
    {
        if (dataShards < 1 || parityShards < 1 || dataShards + parityShards > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(dataShards), "Need 1..254 data shards, at least 1 parity, n <= 255.");
        }

        DataShards = dataShards;
        ParityShards = parityShards;
        _matrix = new byte[TotalShards][];
        for (int r = 0; r < dataShards; r++)
        {
            _matrix[r] = new byte[dataShards];
            _matrix[r][r] = 1;
        }
        for (int i = 0; i < parityShards; i++)
        {
            var row = new byte[dataShards];
            byte xi = (byte)(dataShards + i);
            for (int j = 0; j < dataShards; j++) { row[j] = Inv((byte)(xi ^ (byte)j)); } // 1 / (x_i + y_j)
            _matrix[dataShards + i] = row;
        }
    }

    public byte[][] Encode(ReadOnlySpan<byte> data, out int shardSize)
    {
        shardSize = Math.Max(1, (data.Length + DataShards - 1) / DataShards);
        var shards = new byte[TotalShards][];
        for (int i = 0; i < DataShards; i++)
        {
            shards[i] = new byte[shardSize];
            int offset = i * shardSize;
            if (offset < data.Length)
            {
                data.Slice(offset, Math.Min(shardSize, data.Length - offset)).CopyTo(shards[i]);
            }
        }
        for (int p = 0; p < ParityShards; p++) { shards[DataShards + p] = new byte[shardSize]; }
        ComputeParity(shards, shardSize);
        return shards;
    }

    private void ComputeParity(byte[][] shards, int shardSize)
    {
        for (int p = 0; p < ParityShards; p++)
        {
            var row = _matrix[DataShards + p];
            var output = shards[DataShards + p];
            Array.Clear(output);
            for (int j = 0; j < DataShards; j++)
            {
                byte coef = row[j];
                var input = shards[j];
                for (int b = 0; b < shardSize; b++) { output[b] ^= Mul(coef, input[b]); }
            }
        }
    }

    public byte[][] Reconstruct(byte[]?[] shards, int shardSize)
    {
        if (shards.Length != TotalShards) { throw new ArgumentException($"Expected {TotalShards} shard slots."); }
        if (shards.Any(shard => shard is not null && shard.Length != shardSize)) { throw new ArgumentException($"Every shard must be {shardSize} bytes."); }

        var present = Enumerable.Range(0, TotalShards).Where(i => shards[i] is not null).Take(DataShards).ToArray();
        if (present.Length < DataShards)
        {
            throw new InvalidOperationException($"Only {present.Length} of the {DataShards} required shards are available; data is unrecoverable.");
        }

        var result = new byte[TotalShards][];
        bool allDataPresent = Enumerable.Range(0, DataShards).All(i => shards[i] is not null);
        if (allDataPresent)
        {
            for (int i = 0; i < DataShards; i++) { result[i] = shards[i]!; }
        }
        else
        {
            // Rows of the encoding matrix for the shards we have form an invertible k x k matrix.
            var sub = new byte[DataShards][];
            for (int r = 0; r < DataShards; r++) { sub[r] = (byte[])_matrix[present[r]].Clone(); }
            var inverse = Invert(sub);
            for (int d = 0; d < DataShards; d++)
            {
                var output = new byte[shardSize];
                for (int c = 0; c < DataShards; c++)
                {
                    byte coef = inverse[d][c];
                    if (coef == 0) { continue; }
                    var input = shards[present[c]]!;
                    for (int b = 0; b < shardSize; b++) { output[b] ^= Mul(coef, input[b]); }
                }
                result[d] = output;
            }
        }

        for (int p = 0; p < ParityShards; p++) { result[DataShards + p] = new byte[shardSize]; }
        ComputeParity(result, shardSize);
        return result;
    }

    public byte[] Join(byte[][] shards, int originalLength)
    {
        var output = new byte[originalLength];
        int shardSize = shards[0].Length;
        for (int i = 0; i < DataShards && i * shardSize < originalLength; i++)
        {
            shards[i].AsSpan(0, Math.Min(shardSize, originalLength - i * shardSize)).CopyTo(output.AsSpan(i * shardSize));
        }
        return output;
    }

    private static byte[][] Invert(byte[][] m)
    {
        int n = m.Length;
        var inv = new byte[n][];
        for (int i = 0; i < n; i++) { inv[i] = new byte[n]; inv[i][i] = 1; }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            while (pivot < n && m[pivot][col] == 0) { pivot++; }
            if (pivot == n) { throw new InvalidOperationException("Singular matrix."); }
            (m[col], m[pivot]) = (m[pivot], m[col]);
            (inv[col], inv[pivot]) = (inv[pivot], inv[col]);

            byte scale = Inv(m[col][col]);
            for (int j = 0; j < n; j++) { m[col][j] = Mul(m[col][j], scale); inv[col][j] = Mul(inv[col][j], scale); }

            for (int r = 0; r < n; r++)
            {
                if (r == col || m[r][col] == 0) { continue; }
                byte f = m[r][col];
                for (int j = 0; j < n; j++) { m[r][j] ^= Mul(f, m[col][j]); inv[r][j] ^= Mul(f, inv[col][j]); }
            }
        }
        return inv;
    }
}
