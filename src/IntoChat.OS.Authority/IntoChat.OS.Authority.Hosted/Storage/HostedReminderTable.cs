using IntoChat.OS.Framework;
using IntoChat.OS.Authority.Storage;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace IntoChat.OS.Authority.Hosted;

internal sealed class HostedReminderTable(BrainLogs logs, Serializer serializer, IOptions<HostedRuntimeOptions> options) : IReminderTable
{
    private const string JournalKey = "$hosted-reminders";
    private readonly SemaphoreSlim gate = new(1, 1);
    private HostedReminder[]? entries;
    private long? etag;
    private string Scope => BrainScope.Create(options.Value.AccountId, options.Value.BrainId).Id;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await LoadAsync(); }
        finally { gate.Release(); }
    }

    public Task Init() => StartAsync(CancellationToken.None);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ReminderTableData> ReadRows(GrainId grainId) => ReadAsync(row => row.GrainId == grainId);

    public Task<ReminderTableData> ReadRows(uint begin, uint end) => ReadAsync(row =>
    {
        var hash = row.GrainId.GetUniformHashCode();
        return begin < end ? hash > begin && hash <= end : hash > begin || hash <= end;
    });

    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) =>
        (await ReadAsync(row => row.GrainId == grainId && row.Name == reminderName)).Reminders.SingleOrDefault();

    private async Task<ReminderTableData> ReadAsync(Func<HostedReminder, bool> predicate)
    {
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            return new ReminderTableData(entries!.Where(predicate).Select(row => new ReminderEntry
            { GrainId = row.GrainId, ReminderName = row.Name, StartAt = row.StartAt, Period = row.Period, ETag = row.Tag }));
        }
        finally { gate.Release(); }
    }

    public async Task<string?> UpsertRow(ReminderEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ReminderName);
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            var tag = Guid.NewGuid().ToString("N");
            var row = new HostedReminder(entry.GrainId, entry.ReminderName, entry.StartAt, entry.Period, tag);
            await PersistAsync([.. entries!.Where(existing => existing.GrainId != row.GrainId || existing.Name != row.Name), row]);
            return tag;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag)
    {
        await gate.WaitAsync();
        try
        {
            await LoadAsync();
            if (!entries!.Any(row => row.GrainId == grainId && row.Name == reminderName && row.Tag == eTag)) { return false; }
            await PersistAsync([.. entries!.Where(row => row.GrainId != grainId || row.Name != reminderName)]);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task TestOnlyClearTable()
    {
        await gate.WaitAsync();
        try { await LoadAsync(); await PersistAsync([]); }
        finally { gate.Release(); }
    }

    private async Task LoadAsync()
    {
        if (entries is not null) { return; }
        var record = (await logs.For(Scope)).Read(JournalKey, "table");
        entries = record is null ? [] : serializer.Deserialize<HostedReminder[]>(record.Record);
        etag = record?.ETag;
    }

    private async Task PersistAsync(HostedReminder[] next)
    {
        var log = await logs.For(Scope);
        etag = await log.Append(JournalKey, "table", etag, BrainRecordChange.Write, serializer.SerializeToArray(next), [], CancellationToken.None);
        entries = next;
    }
}

[GenerateSerializer, Alias("hosted.reminder.v1")]
internal sealed record HostedReminder([property: Id(0)] GrainId GrainId, [property: Id(1)] string Name,
    [property: Id(2)] DateTime StartAt, [property: Id(3)] TimeSpan Period, [property: Id(4)] string Tag);
