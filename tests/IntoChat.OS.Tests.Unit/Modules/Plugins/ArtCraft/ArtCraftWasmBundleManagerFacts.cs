using System.IO.Compression;
using System.Net;
using System.Text;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.ArtCraft;

namespace IntoChat.OS.Tests.Unit.Modules.ArtCraft;

public sealed class ArtCraftWasmBundleManagerFacts
{
    [Fact]
    public async Task EnsureInstalledDownloadsExtractsAndCachesWasmBundle()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"artcraft-wasm-test-{Guid.NewGuid():N}");
        try
        {
            var zipBytes = CreateSampleWasmZip();
            var handler = new StubZipHandler(zipBytes);
            using var http = new HttpClient(handler);

            var descriptor = ArtCraftCatalog.Get("photocraft");

            var ct = TestContext.Current.CancellationToken;
            var first = await ArtCraftWasmBundleManager.EnsureInstalledAsync(descriptor, tempRoot, http, ct);
            Assert.True(first.Installed);
            Assert.NotNull(first.DirectoryPath);
            Assert.True(File.Exists(Path.Combine(first.DirectoryPath!, "index.html")));
            Assert.True(File.Exists(Path.Combine(first.DirectoryPath!, "app.wasm")));
            Assert.Equal(1, handler.Calls);

            // Second call must hit cache without re-downloading.
            var second = await ArtCraftWasmBundleManager.EnsureInstalledAsync(descriptor, tempRoot, http, ct);
            Assert.True(second.Installed);
            Assert.Equal(first.DirectoryPath, second.DirectoryPath);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) { Directory.Delete(tempRoot, recursive: true); }
        }
    }

    private static byte[] CreateSampleWasmZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var idx = zip.CreateEntry("dist/index.html");
            using (var w = new StreamWriter(idx.Open(), Encoding.UTF8))
            {
                w.Write("<!doctype html><html><head></head><body>PhotoCraft WASM</body></html>");
            }

            var wasm = zip.CreateEntry("dist/app.wasm");
            using var s = wasm.Open();
            s.Write([0x00, 0x61, 0x73, 0x6D]);
        }
        return ms.ToArray();
    }

    private sealed class StubZipHandler(byte[] payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
