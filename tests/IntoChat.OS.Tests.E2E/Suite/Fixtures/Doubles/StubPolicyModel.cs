using System.Collections.Concurrent;

namespace IntoChat.OS.Tests.E2E.Fixtures.Doubles;

/// <summary>
/// In-memory policy model double for simulating AI completions, tool selections, and decision policies.
/// </summary>
public sealed class StubPolicyModel
{
    private readonly ConcurrentDictionary<string, string> _cannedResponses = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<string> _promptsReceived = new();

    public IReadOnlyCollection<string> PromptsReceived => _promptsReceived;

    public void RegisterResponse(string promptKey, string response)
    {
        _cannedResponses[promptKey] = response;
    }

    public Task<string> CompleteAsync(string prompt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _promptsReceived.Add(prompt);

        foreach (var (key, value) in _cannedResponses)
        {
            if (prompt.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(value);
            }
        }

        return Task.FromResult("{\"action\": \"default\", \"reason\": \"no canned response\"}");
    }
}
