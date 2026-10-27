namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// Stream chunks cross the same trusted test edge as the fixture's initial IStorage call.
internal sealed class DriveOwnerStream(Stream inner, Person owner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { using (owner.AsCaller()) { return await inner.ReadAsync(buffer, cancellationToken); } }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { using (owner.AsCaller()) { await inner.WriteAsync(buffer, cancellationToken); } }
    public override int Read(byte[] buffer, int offset, int count)
    { using (owner.AsCaller()) { return inner.Read(buffer, offset, count); } }
    public override void Write(byte[] buffer, int offset, int count)
    { using (owner.AsCaller()) { inner.Write(buffer, offset, count); } }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); }
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
