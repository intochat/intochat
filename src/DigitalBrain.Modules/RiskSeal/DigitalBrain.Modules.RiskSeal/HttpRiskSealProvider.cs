using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DigitalBrain.RiskSeal;

// ASSUMED wire shape (RiskSeal's contract is pending): POST {base}v1/score and GET {base}v1/account,
// both with Authorization: Bearer {ApiKey}. See README.md.
internal sealed class HttpRiskSealProvider(IHttpClientFactory clients) : IRiskSealProvider
{
    public Task<RiskSealReply> ScoreAsync(Uri baseUrl, string apiKey, ScoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "v1/score"))
        {
            Content = JsonContent.Create(new { email = request.Email, phone = request.Phone, ip = request.IpAddress, document = request.DocumentNumber }),
        };
        return SendAsync(message, apiKey, cancellationToken);
    }

    public Task<RiskSealReply> ReadAccountAsync(Uri baseUrl, string apiKey, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "v1/account")), apiKey, cancellationToken);

    private async Task<RiskSealReply> SendAsync(HttpRequestMessage message, string apiKey, CancellationToken cancellationToken)
    {
        using (message)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            try
            {
                using var response = await clients.CreateClient(RiskSealModule.HttpClientName).SendAsync(message, cancellationToken);
                return new((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, "timeout"); }
            catch (HttpRequestException) { return new(null, "unreachable"); }
        }
    }
}
