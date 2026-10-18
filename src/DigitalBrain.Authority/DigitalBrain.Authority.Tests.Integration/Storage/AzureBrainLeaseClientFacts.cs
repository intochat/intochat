using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using DigitalBrain.Authority.Storage;

namespace DigitalBrain.Authority.Tests.Integration.Storage;

// Status mapping against a fake Blob endpoint: refusals are answers, anything else is a transport failure left to the fence.
public sealed class AzureBrainLeaseClientFacts
{
    [Fact]
    public async Task AcquireReturnsTheLeaseIdOrNullWhenHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new FakeBlobService { Upload = HttpStatusCode.Conflict, Lease = HttpStatusCode.Created };
        Assert.Equal(service.LeaseId, await Client(service).TryAcquireAsync("alice", TimeSpan.FromSeconds(60), ct));

        service.Lease = HttpStatusCode.Conflict;
        Assert.Null(await Client(service).TryAcquireAsync("alice", TimeSpan.FromSeconds(60), ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.PreconditionFailed, false)]
    public async Task RenewalMapsRefusalsToFalse(HttpStatusCode status, bool accepted)
    {
        var service = new FakeBlobService { Lease = status };
        Assert.Equal(accepted, await Client(service).RenewAsync("alice", service.LeaseId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenewalServerErrorsAreTransportFailures()
    {
        var service = new FakeBlobService { Lease = HttpStatusCode.InternalServerError };
        await Assert.ThrowsAsync<RequestFailedException>(() => Client(service).RenewAsync("alice", service.LeaseId, TestContext.Current.CancellationToken));
    }

    private static AzureBrainLeaseClient Client(FakeBlobService service)
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(service)) };
        options.Retry.MaxRetries = 0;
        return new(new BlobContainerClient(new Uri("https://account.blob.core.windows.net/brainlease-v1"), options));
    }

    private sealed class FakeBlobService : HttpMessageHandler
    {
        public string LeaseId { get; } = Guid.NewGuid().ToString();
        public HttpStatusCode Upload { get; set; } = HttpStatusCode.Created;
        public HttpStatusCode Lease { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var lease = request.RequestUri!.Query.Contains("comp=lease", StringComparison.Ordinal);
            var response = new HttpResponseMessage(lease ? Lease : Upload) { Content = new ByteArrayContent([]) };
            if (lease) { response.Headers.Add("x-ms-lease-id", LeaseId); }
            return Task.FromResult(response);
        }
    }
}
