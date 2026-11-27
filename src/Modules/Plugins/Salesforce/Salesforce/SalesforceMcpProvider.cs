using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Modules.Mcp;

namespace IntoChat.OS.Modules.Salesforce;

internal sealed class SalesforceMcpProvider(Uri? endpoint, HttpMessageHandler? httpHandler = null) : ISalesforceProvider
{
    internal static readonly string[] AdmittedTools = ["getUserInfo", "getObjectSchema", "soqlQuery", "createRecord", "updateRecord"];
    private readonly McpClientSession _session = new(httpHandler);

    public async Task<JsonElement> InvokeAsync(string tool, JsonElement arguments,
        string accessToken, CancellationToken cancellationToken)
    {
        if (!AdmittedTools.Contains(tool, StringComparer.Ordinal))
        {
            throw new SalesforceUnavailableException("This Salesforce operation is not allowed.");
        }
        ValidateToken(accessToken);
        if (endpoint is null)
        {
            throw new SalesforceUnavailableException("Salesforce MCP is not configured.");
        }
        var normalized = arguments.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        try
        {
            var headers = McpClientSession.BuildHeaders("Authorization", "Bearer ", accessToken);
            return await _session.RunEphemeralAsync(endpoint, headers, async (client, token) =>
            {
                var result = await client.CallToolAsync(tool, normalized, cancellationToken: token).ConfigureAwait(false);
                var envelope = McpClientSession.SerializeCallResult(result);
                if (Encoding.UTF8.GetByteCount(envelope.GetRawText()) > 128 * 1024)
                {
                    throw new SalesforceUnavailableException("Salesforce response exceeds the provider response budget.");
                }
                if (result.IsError == true)
                {
                    if (tool is "createRecord" or "updateRecord")
                    {
                        return MarkUntrusted(envelope);
                    }
                    throw new SalesforceUnavailableException("Salesforce returned a provider error.");
                }
                return MarkUntrusted(ReadContent(envelope, tool == "getObjectSchema"));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SalesforceUnavailableException) { throw; }
        catch (SalesforceUnreachableException) { throw; }
        catch (ArgumentException)
        {
            throw new SalesforceUnavailableException("Salesforce MCP endpoint is not allowed.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SalesforceUnavailableException("Salesforce returned an invalid response shape.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            throw new SalesforceUnreachableException("Salesforce is unavailable. Check service access and try again later.");
        }
    }

    public async Task<string> ReadToolSchemaHashAsync(string tool, string accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AdmittedTools.Contains(tool, StringComparer.Ordinal))
        {
            throw new SalesforceUnavailableException("This Salesforce operation is not allowed.");
        }
        ValidateToken(accessToken);
        if (endpoint is null)
        {
            throw new SalesforceUnavailableException("Salesforce MCP is not configured.");
        }
        try
        {
            var headers = McpClientSession.BuildHeaders("Authorization", "Bearer ", accessToken);
            var catalog = await _session.ReadCatalogAsync(endpoint, headers, cancellationToken).ConfigureAwait(false);
            var selected = catalog.SingleOrDefault(item => item.Name == tool)
                ?? throw new SalesforceUnavailableException("The authenticated Salesforce catalog does not contain this tool.");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                selected.JsonSchema.GetRawText() + "\n" + selected.ReturnJsonSchema?.GetRawText())));
        }
        catch (SalesforceUnavailableException) { throw; }
        catch (SalesforceUnreachableException) { throw; }
        catch (ArgumentException)
        {
            throw new SalesforceUnavailableException("Salesforce MCP endpoint is not allowed.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SalesforceUnavailableException("Salesforce returned an invalid response shape.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            throw new SalesforceUnreachableException("Salesforce is unavailable. Check service access and try again later.");
        }
    }

    private static void ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384 || token.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new SalesforceUnavailableException("Salesforce did not issue a valid bearer token.");
        }
    }

    private static JsonElement MarkUntrusted(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            content = JsonSerializer.SerializeToElement(new { data = content });
        }
        var result = JsonNode.Parse(content.GetRawText())!.AsObject();
        // Screened at the provider boundary before the assistant sees the payload.
        result["untrustedData"] = true;
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonElement ReadContent(JsonElement envelope, bool allowText = false)
    {
        if (envelope.TryGetProperty("structuredContent", out var structured))
        {
            return structured;
        }
        if (envelope.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            var text = string.Join('\n', blocks.EnumerateArray()
                .Where(static block => block.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(static block => block.GetProperty("text").GetString()));
            try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
            catch (JsonException) when (allowText && !string.IsNullOrWhiteSpace(text))
            { return JsonSerializer.SerializeToElement(new { text }); }
        }
        throw new SalesforceUnavailableException("Salesforce MCP returned an invalid response shape.");
    }
}
