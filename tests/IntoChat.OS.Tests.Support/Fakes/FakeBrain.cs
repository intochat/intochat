using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain.Capabilities.Storage.Confinement;
using IntoChat.Sdk;

namespace IntoChat.OS.Tests.Support.Fakes;

/// <summary>
/// Deterministic in-memory <see cref="ISandboxBrain"/> implementation used by scenario verification and tests
/// to run compiled <see cref="BrainApp{TState, TSettings}"/> logic without touching host disk or network.
/// </summary>
public sealed class FakeBrain(string brainId = "test-brain", string appKey = "test-app") : IntoChat.Sdk.ISandboxBrain
{
    private readonly ConcurrentDictionary<string, FakeFolder> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _slots = new(StringComparer.Ordinal);
    private readonly List<DigitalBrain.Signal> _publishedSignals = [];
    private readonly List<string> _logs = [];

    public string BrainId { get; } = brainId;
    public string AppKey { get; } = appKey;
    public FakeNotifications NotificationsFake { get; } = new(brainId, appKey);
    public FakeAi AiFake { get; } = new();
    public FakeTables TablesFake { get; } = new();

    public INotifications Notifications => NotificationsFake;
    public IAi Ai => AiFake;
    public ITables Tables => TablesFake;

    public IReadOnlyList<DigitalBrain.Signal> PublishedSignals => _publishedSignals;
    public IReadOnlyList<string> Logs => _logs;

    public FakeFolder ConfigureFolder(string slot, FolderAccess access = FolderAccess.ReadWrite)
        => _folders.GetOrAdd(slot, _ => new FakeFolder(access));

    public void ConfigureSlot<TContract>(string slot, TContract instance) where TContract : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _slots[$"{typeof(TContract).FullName}:{slot}"] = instance;
    }

    public IFolder Folder(string slot = "folder")
        => _folders.TryGetValue(slot, out var folder)
            ? folder
            : throw new UnauthorizedAccessException($"Folder slot '{slot}' is not bound in FakeBrain.");

    public TContract Slot<TContract>(string? slot = null) where TContract : class
    {
        var key = $"{typeof(TContract).FullName}:{slot ?? typeof(TContract).Name}";
        if (_slots.TryGetValue(key, out var found) && found is TContract typed)
        {
            return typed;
        }

        if (typeof(TContract) == typeof(INotifications))
        {
            return (TContract)(object)NotificationsFake;
        }

        if (typeof(TContract) == typeof(IAi))
        {
            return (TContract)(object)AiFake;
        }

        if (typeof(TContract) == typeof(ITables))
        {
            return (TContract)(object)TablesFake;
        }

        throw new InvalidOperationException($"Slot '{key}' is not configured in FakeBrain.");
    }

    public Task PublishAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default) where TSignal : DigitalBrain.Signal
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(signal);
        _publishedSignals.Add(signal);
        return Task.CompletedTask;
    }

    public Task LogAsync(string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logs.Add(message);
        return Task.CompletedTask;
    }

    public sealed class FakeNotifications(string brainId, string appKey) : INotifications
    {
        private readonly List<NotificationItem> _items = [];

        public IReadOnlyList<NotificationItem> Sent => _items;

        public Task<NotificationReceipt> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(request);
            if (!string.IsNullOrWhiteSpace(request.DedupeKey)
                && _items.FirstOrDefault(item => item.Category == request.Category && item.Title == request.Title) is { } existing)
            {
                return Task.FromResult(new NotificationReceipt(existing.Id, Deduplicated: true, existing.CreatedUtc));
            }

            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            _items.Add(new NotificationItem(id, brainId, appKey, request.Title, request.Body, request.Priority, request.Category, request.ActionUrl, false, now, null));
            return Task.FromResult(new NotificationReceipt(id, Deduplicated: false, now));
        }

        public Task<NotificationReceipt> NotifyAsync(string title, string body, NotificationPriority priority = NotificationPriority.Normal, CancellationToken cancellationToken = default)
            => SendAsync(new NotificationRequest(title, body, priority), cancellationToken);

        public Task<NotificationItem[]> ListAsync(int limit = 50, bool unreadOnly = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<NotificationItem> query = _items;
            if (unreadOnly)
            {
                query = query.Where(item => !item.Read);
            }

            return Task.FromResult(query.Take(limit).ToArray());
        }

        public Task MarkReadAsync(string notificationId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idx = _items.FindIndex(item => item.Id == notificationId);
            if (idx >= 0)
            {
                _items[idx] = _items[idx] with { Read = true, ReadUtc = DateTimeOffset.UtcNow };
            }

            return Task.CompletedTask;
        }

        public Task MarkAllReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < _items.Count; i++)
            {
                _items[i] = _items[i] with { Read = true, ReadUtc = _items[i].ReadUtc ?? now };
            }

            return Task.CompletedTask;
        }
    }

    public sealed class FakeFolder(FolderAccess access) : IFolder
    {
        private static readonly string FakeRoot = PathConfinement.Normalize(
            Path.Combine(Path.GetTempPath(), "intochat-fake-root"));

        private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void SeedText(string relativePath, string content)
            => _files[Normalize(relativePath)] = System.Text.Encoding.UTF8.GetBytes(content);

        public Task<FolderEntry[]> ListAsync(string relativePath = "", CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = string.IsNullOrWhiteSpace(relativePath) ? "" : Normalize(relativePath).TrimEnd('/') + "/";
            var entries = _files
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(pair =>
                {
                    var name = pair.Key.Contains('/') ? pair.Key[(pair.Key.LastIndexOf('/') + 1)..] : pair.Key;
                    return new FolderEntry(name, pair.Key, false, pair.Value.Length, DateTimeOffset.UtcNow);
                })
                .ToArray();
            return Task.FromResult(entries);
        }

        public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Normalize(relativePath);
            return _files.TryGetValue(key, out var bytes)
                ? Task.FromResult(System.Text.Encoding.UTF8.GetString(bytes))
                : throw new FileNotFoundException($"File '{relativePath}' not found.", key);
        }

        public Task<byte[]> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Normalize(relativePath);
            return _files.TryGetValue(key, out var bytes)
                ? Task.FromResult(bytes.ToArray())
                : throw new FileNotFoundException($"File '{relativePath}' not found.", key);
        }

        public Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireWrite();
            _files[Normalize(relativePath)] = System.Text.Encoding.UTF8.GetBytes(content);
            return Task.CompletedTask;
        }

        public Task WriteBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireWrite();
            _files[Normalize(relativePath)] = content.ToArray();
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_files.ContainsKey(Normalize(relativePath)));
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireWrite();
            _files.TryRemove(Normalize(relativePath), out _);
            return Task.CompletedTask;
        }

        public Task MoveAsync(string sourceRelativePath, string destinationRelativePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireWrite();
            var src = Normalize(sourceRelativePath);
            var dst = Normalize(destinationRelativePath);
            if (!_files.TryRemove(src, out var bytes))
            {
                throw new FileNotFoundException($"File '{sourceRelativePath}' not found.", src);
            }

            _files[dst] = bytes;
            return Task.CompletedTask;
        }

        private void RequireWrite()
        {
            if (access != FolderAccess.ReadWrite)
            {
                throw new UnauthorizedAccessException("Folder binding is read-only.");
            }
        }

        private static string Normalize(string relativePath)
        {
            try
            {
                var resolved = PathConfinement.Resolve([FakeRoot], relativePath);
                return Path.GetRelativePath(FakeRoot, resolved).Replace('\\', '/');
            }
            catch (ConfinementDenied ex)
            {
                throw new UnauthorizedAccessException(ex.Message, ex);
            }
        }
    }

    public sealed class FakeAi : IAi
    {
        public Queue<string> Responses { get; } = new();
        public List<string> Prompts { get; } = [];

        public Task<string> AskAsync(string prompt, string? systemPrompt = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prompts.Add(prompt);
            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : "{}");
        }

        public async Task<T> ExtractAsync<T>(string prompt, CancellationToken cancellationToken = default)
        {
            var json = await AskAsync(prompt, null, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json)!;
        }
    }

    public sealed class FakeTables : ITables
    {
        private readonly ConcurrentDictionary<string, FakeTableHandle> _tables = new(StringComparer.Ordinal);

        public ITableHandle Table(string name)
            => _tables.GetOrAdd(name, _ => new FakeTableHandle());

        public sealed class FakeTableHandle : ITableHandle
        {
            private readonly ConcurrentDictionary<string, string> _rows = new(StringComparer.Ordinal);

            public Task EnsureAsync(TableColumnSpec[] columns, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task UpsertAsync(string key, string jsonRow, CancellationToken cancellationToken = default)
            {
                _rows[key] = jsonRow;
                return Task.CompletedTask;
            }

            public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
                => Task.FromResult(_rows.TryGetValue(key, out var json) ? json : null);

            public Task<TableRow[]> ListAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
                => Task.FromResult(_rows.Skip(offset).Take(limit).Select(pair => new TableRow(pair.Key, pair.Value)).ToArray());

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
                => Task.FromResult(_rows.TryRemove(key, out _));
        }
    }
}
