using System.Text;
using System.Text.Json;

namespace IntoChat.OS.LocalSetup;

// A recorded machine: JSON fixture of command results, HTTP answers, free space, bound ports, OS, environment and files.
// Command key is "file arg arg"; HTTP key is "METHOD absolute-url". Unknown commands exit 127; unknown URLs refuse the connection.
public sealed class FakeProbeEnvironment : IProbeEnvironment
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly FakeMachine _machine;

    private FakeProbeEnvironment(FakeMachine machine)
    {
        _machine = machine;
        Http = new FakeHandler(machine.Http ?? []);
    }

    public bool IsWindows => !string.Equals(_machine.Os, "linux", StringComparison.OrdinalIgnoreCase);

    public HttpMessageHandler Http { get; }

    public static FakeProbeEnvironment Load(string path) => FromJson(File.ReadAllText(path));

    public static FakeProbeEnvironment FromJson(string json)
        => new(JsonSerializer.Deserialize<FakeMachine>(json, JsonOptions) ?? throw new JsonException("Empty fixture."));

    public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var key = string.Join(' ', [file, .. args]);
        if (_machine.Commands is null || !_machine.Commands.TryGetValue(key, out var command))
        {
            return Task.FromResult(new ProcessResult(ProcessResult.NotFound, [], "not found"));
        }
        var stdout = command.StdoutBase64 is not null ? Convert.FromBase64String(command.StdoutBase64) : Encoding.UTF8.GetBytes(command.Stdout ?? string.Empty);
        return Task.FromResult(new ProcessResult(command.Exit, stdout, command.Stderr ?? string.Empty));
    }

    public long? FreeBytes(string path)
    {
        var free = _machine.FreeBytes;
        if (free is null) { return null; }
        return free.TryGetValue(path, out var exact) ? exact : free.TryGetValue("*", out var any) ? any : null;
    }

    public bool CanBind(int port) => _machine.BoundPorts?.Contains(port) != true;

    public string? Env(string name) => _machine.Env is not null && _machine.Env.TryGetValue(name, out var value) ? value : null;

    public string? ReadFile(string path) => _machine.Files is not null && _machine.Files.TryGetValue(path, out var value) ? value : null;

    private sealed record FakeMachine(
        string? Os,
        Dictionary<string, FakeCommand>? Commands,
        Dictionary<string, FakeResponse>? Http,
        Dictionary<string, long>? FreeBytes,
        int[]? BoundPorts,
        Dictionary<string, string>? Env,
        Dictionary<string, string>? Files);

    private sealed record FakeCommand(int Exit, string? Stdout, string? StdoutBase64, string? Stderr);

    private sealed record FakeResponse(int Status, string? Body);

    private sealed class FakeHandler(Dictionary<string, FakeResponse> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri}";
            if (!responses.TryGetValue(key, out var response))
            {
                throw new HttpRequestException($"Connection refused: {key}");
            }
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)response.Status) { Content = new StringContent(response.Body ?? string.Empty) });
        }
    }
}
