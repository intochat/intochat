using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Sdk.Http.Compute;

namespace IntoChat.OS.Sdk.SampleModule;

public sealed class SampleHttpClient(HttpClient client)
{
    public async Task<HttpResponseMessage> ReadAsync(SampleOptions options, Funding funding, string invocationId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateReadRequest(options, funding, invocationId);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public static HttpRequestMessage CreateReadRequest(SampleOptions options, Funding funding, string invocationId, CostSubject? subject = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var query = "items?limit=" + options.PageSize.ToString(CultureInfo.InvariantCulture) + "&region=" + Uri.EscapeDataString(options.Region);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Endpoint, query));
        request.SetExternalCall(new("sample.read", funding, subject, invocationId));
        return request;
    }
}
