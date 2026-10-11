using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.Monnet;

internal static class MonnetJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

// ASSUMED REST shape until Monnet's API documentation arrives (README, "Assumed API"):
//   POST {BaseUrl}v1/payments        X-Api-Key, X-Merchant-Id; JSON body -> the payment
//   GET  {BaseUrl}v1/payments/{id}   -> the payment
//   GET  {BaseUrl}v1/merchants/me    -> 200 when the key works
internal sealed class HttpMonnetProvider(IHttpClientFactory clients, TimeProvider time) : IMonnetProvider
{
    public async Task<PaymentOrder> CreatePaymentOrderAsync(MonnetAccount account, CreatePaymentOrder command, CancellationToken ct)
    {
        var body = new CreatePaymentBody(account.MerchantId, command.Amount, command.Currency, command.Reference, command.Description,
            command.PayerPhone is null && command.PayerDni is null ? null : new PayerBody(command.PayerPhone, command.PayerDni));
        using var request = Request(HttpMethod.Post, account, "v1/payments");
        request.Content = JsonContent.Create(body, options: MonnetJson.Options);
        var payment = await SendAsync(request, ct);
        return new(payment.Id ?? throw Invalid(), payment.Reference ?? command.Reference, payment.Amount ?? command.Amount,
            payment.Currency ?? command.Currency, payment.Status ?? "created", payment.CheckoutUrl, time.GetUtcNow());
    }

    public async Task<PaymentStatus> ReadPaymentAsync(MonnetAccount account, string paymentId, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, account, "v1/payments/" + Uri.EscapeDataString(paymentId));
        var payment = await SendAsync(request, ct);
        return new(payment.Id ?? paymentId, payment.Reference ?? "", payment.Status ?? throw Invalid(), payment.Amount ?? 0m, payment.Currency ?? "PEN");
    }

    public async Task<string?> ProbeAsync(MonnetAccount account, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, account, "v1/merchants/me");
        try
        {
            using var response = await Client.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : Describe(response.StatusCode);
        }
        catch (Exception error) when (Transport(error, ct) is { } detail) { return detail; }
    }

    private HttpClient Client => clients.CreateClient(MonnetModule.HttpClientName);

    private static HttpRequestMessage Request(HttpMethod method, MonnetAccount account, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(account.BaseUrl, path));
        request.Headers.Add("X-Api-Key", account.ApiKey);
        request.Headers.Add("X-Merchant-Id", account.MerchantId);
        return request;
    }

    private async Task<PaymentBody> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await Client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) { throw new MonnetUnavailableException(Describe(response.StatusCode)); }
            return await response.Content.ReadFromJsonAsync<PaymentBody>(MonnetJson.Options, ct) ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
        catch (Exception error) when (Transport(error, ct) is { } detail) { throw new MonnetUnavailableException(detail); }
    }

    // Only the status survives: a provider body or exception message could echo the key.
    internal static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"unauthorized ({(int)status})",
        HttpStatusCode.NotFound => "not found (404)",
        _ when (int)status >= 500 => $"unavailable ({(int)status})",
        _ => $"refused ({(int)status})",
    };

    private static string? Transport(Exception error, CancellationToken ct) => error switch
    {
        TaskCanceledException when !ct.IsCancellationRequested => "timeout",
        HttpRequestException => "unreachable",
        _ => null,
    };

    private static MonnetUnavailableException Invalid() => new("invalid response");

    private sealed record PayerBody(string? Phone, string? Dni);
    private sealed record CreatePaymentBody(string MerchantId, decimal Amount, string Currency, string Reference, string Description, PayerBody? Payer);
    private sealed record PaymentBody(string? Id, string? Reference, decimal? Amount, string? Currency, string? Status, string? CheckoutUrl);
}
