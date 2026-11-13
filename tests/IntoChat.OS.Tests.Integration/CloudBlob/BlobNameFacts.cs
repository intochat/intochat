using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using DigitalBrain.Capabilities.Storage;

namespace IntoChat.Cloud.Blob.Tests.Integration;

public sealed class BlobNameFacts
{
    private static readonly Regex ContainerName = new("^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant);

    [Fact]
    public void ContainerNameIsHashOfTheBrain()
    {
        var name = BlobContainerName.For("brain-1");
        Assert.Equal(42, name.Length);
        Assert.StartsWith("b-", name);
        Assert.Matches(ContainerName, name);
        Assert.Equal(name, BlobContainerName.For("brain-1"));
        Assert.NotEqual(name, BlobContainerName.For("brain-2"));
        Assert.DoesNotContain("--", name);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    public void HostileSegmentsAreDenied(string relative)
    {
        var error = Assert.Throws<Denied>(() => BlobItemId.Read(BlobItemId.Of(relative.TrimEnd('/'))));
        Assert.Equal("denied", error.Code);
    }

    [Fact]
    public void BackslashControlAndLongNamesAreDenied()
    {
        Assert.Equal("denied", Assert.Throws<Denied>(() => BlobItemId.Read(Raw("a\\b"))).Code);
        Assert.Equal("denied", Assert.Throws<Denied>(() => BlobItemId.Read(BlobItemId.Of("a\u0001b"))).Code);
        Assert.Equal("denied", Assert.Throws<Denied>(() => BlobItemId.Read(BlobItemId.Of(new string('a', 1025)))).Code);
        Assert.Equal("", BlobItemId.Read(BlobItemId.Of("")));
    }

    [Fact]
    public void ForeignPrefixIsNotFound()
    {
        var error = Assert.Throws<NotFound>(() => BlobItemId.Read(new ItemId("nope")));
        Assert.Equal("not-found", error.Code);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("local", false)]
    [InlineData("cloud", true)]
    [InlineData("local+cloud", true)]
    [InlineData("local-cloud", true)]
    [InlineData("CLOUD", true)]
    public void LoadsUnderCloudTopologiesOnly(string? topology, bool loads) =>
        Assert.Equal(loads, BlobStorageModule.LoadsUnder(topology));

    [Fact]
    public async Task QuotaIsUnknownNeverZero()
    {
        if (!await PortOpen()) { Assert.Skip("Azurite is not listening on 127.0.0.1:10000."); }
        var container = BlobStorage.Open(BlobStorage.AzuriteConnection).GetBlobContainerClient(BlobContainerName.For(Guid.NewGuid().ToString("N")));
        var storage = new BlobStorage(container, operations: null);
        var quota = (await storage.Describe(TestContext.Current.CancellationToken)).Quota;
        Assert.Equal(QuotaState.Unknown, quota.State);
        Assert.Null(quota.Used);
        Assert.Null(quota.Total);
    }

    private static ItemId Raw(string text)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new ItemId(BlobItemId.Prefix + encoded);
    }

    private static async Task<bool> PortOpen()
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 10000, TestContext.Current.CancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException) { return false; }
    }
}
