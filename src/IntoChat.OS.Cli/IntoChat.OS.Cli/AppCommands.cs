using System.Net.Http.Headers;
using System.Threading.Channels;
using DigitalBrain.Apps.Packaging;
using static IntoChat.OS.Cli.OwnerSessionClient;

namespace IntoChat.OS.Cli;

internal sealed class AppCommands(OwnerSessionClient client, TextReader input, TextWriter output, bool interactive)
{
    private Task Log(string message, CancellationToken ct) => output.WriteLineAsync(message.AsMemory(), ct);
    private static bool Same(Review review, Upload upload) => review.Requires.Consent() == upload.Requires.Consent()
        && review.HostAdapters.Order(StringComparer.Ordinal).SequenceEqual(upload.HostAdapters.Order(StringComparer.Ordinal));
    private async Task Consent(Review review, string message, CancellationToken ct)
    {
        await Log($"{message}\nBrain: {review.Brain}\nApp: {review.Name}\nSHA256: {review.Digest}\nRequirements: {System.Text.Json.JsonSerializer.Serialize(review.Requires, Json)}\nHost adapters: {string.Join(", ", review.HostAdapters)}\nReplaces existing app: {review.ReplacesExisting}\nType INSTALL to trust this code, accept these requirements and approve this replacement:", ct);
        if (!interactive || await input.ReadLineAsync(ct) != "INSTALL") { throw new UnauthorizedAccessException("Owner approval was not given. Interactive input is required."); }
    }
    internal async Task<Snapshot> Install(Stream archive, CancellationToken ct, Func<bool>? current = null)
    {
        var upload = await client.Stage(archive, ct);
        return await InstallReviewed(upload, ct, current);
    }
    private async Task<Snapshot> InstallReviewed(Upload upload, CancellationToken ct, Func<bool>? current)
    {
        var name = upload.Id.Split('.').Last().ToLowerInvariant();
        var review = await client.Post<Review>(client.Route + "/review", new { upload.Digest, Name = name }, ct);
        if (review.Digest != upload.Digest || review.Brain != client.Brain || review.Name != name || !Same(review, upload))
        { throw new InvalidDataException("The server review does not match the uploaded package."); }
        await Consent(review, upload.TrustMessage, ct);
        if (current is not null && !current()) { throw new StaleCandidateException(); }
        return await client.Post<Snapshot>(client.Route + "/activate", new { Name = name, Approval = new { ReviewToken = review.Token, review.Digest, OperationId = Guid.NewGuid(), TrustCode = true, RequirementsConsent = review.Requires.Consent(), ReplaceExisting = review.ReplacesExisting } }, ct);
    }
    internal async Task Run(string project, CancellationToken ct)
    {
        project = Path.GetFullPath(project);
        var watch = new AppWatch();
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var files = new FileSystemWatcher(Path.GetDirectoryName(project)!) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
        void Changed(object sender, FileSystemEventArgs args) { if (AppWatch.Includes(args.FullPath)) { watch.Invalidate(); changes.Writer.TryWrite(true); } }
        files.Changed += Changed; files.Created += Changed; files.Deleted += Changed; files.Renamed += Changed;
        files.Error += (_, _) => { watch.Invalidate(); changes.Writer.TryWrite(true); };
        files.EnableRaisingEvents = true;
        Lease? lease = null;
        var leaseTokens = new HashSet<string>(StringComparer.Ordinal);
        Generation? generation = null;
        string? name = null;
        string? consent = null;
        string[] adapters = [];
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Renew()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var current = lease;
                if (current is not null)
                {
                    try
                    {
                        var renewed = await client.Post<Lease>(client.Route + "/development/renew", new { current.Token, Project = project }, lifetime.Token);
                        if (ReferenceEquals(lease, current)) { lease = renewed; }
                    }
                    catch (Exception error) when (!ReferenceEquals(lease, current))
                    { await Log($"Renewal for a replaced development lease was discarded: {error.GetType().Name}", CancellationToken.None); }
                }
            }
        }
        var renewal = Renew();
        try
        {
            changes.Writer.TryWrite(true);
            while (await changes.Reader.WaitToReadAsync(ct))
            {
                while (changes.Reader.TryRead(out _)) { }
                // Resettable debounce; edits invalidate immediately, including while build or review is running.
                await Task.Delay(150, ct);
                if (changes.Reader.TryPeek(out _)) { continue; }

                try
                {
                    await watch.Change(token => new AppBuild().Build(project, "Debug", token), async (package, current, token) =>
                    {
                        await using var archive = new MemoryStream();
                        await new AppPackageWriter().Write(package.Descriptor, package.DirectoryPath, archive, ct);
                        archive.Position = 0;
                        var uploaded = await client.Stage(archive, ct);
                        if (!current()) { return; }
                        if (lease is null || generation is null || consent != uploaded.Requires.Consent() || !adapters.Order(StringComparer.Ordinal).SequenceEqual(uploaded.HostAdapters.Order(StringComparer.Ordinal)))
                        {
                            var previous = lease;
                            lease = null;
                            if (previous is not null)
                            {
                                await client.Post<object>(client.Route + "/development/revoke", new { previous.Token, Project = project }, ct);
                                leaseTokens.Remove(previous.Token);
                            }
                            var snapshot = await InstallReviewed(uploaded, ct, current);
                            generation = snapshot.ActiveCompiled ?? throw new InvalidDataException("Activation returned no compiled generation.");
                            name = uploaded.Id.Split('.').Last().ToLowerInvariant();
                            consent = uploaded.Requires.Consent(); adapters = uploaded.HostAdapters;
                            await Log($"Trust future builds of {project} for this development session? Type TRUST PROJECT:", ct);
                            if (!interactive || await input.ReadLineAsync(ct) != "TRUST PROJECT") { throw new UnauthorizedAccessException("Project trust was not given."); }
                            lease = await client.Post<Lease>(client.Route + "/development/begin", new { Name = name, Project = project, generation.OperationId, ExpectedRevision = generation.Revision, TrustProject = true }, ct);
                            leaseTokens.Add(lease.Token);
                        }
                        else
                        {
                            if (!current()) { return; }
                            var snapshot = await client.Post<Snapshot>(client.Route + "/development/reload", new { Name = name, Reload = new { LeaseToken = lease.Token, Project = project, uploaded.Digest, ExpectedRevision = generation.Revision, OperationId = Guid.NewGuid() } }, ct);
                            generation = snapshot.ActiveCompiled ?? throw new InvalidDataException("Reload returned no generation.");
                        }
                        await Log($"Active: {uploaded.Digest}", ct);
                    }, ct);
                }
                catch (StaleCandidateException)
                { await Log("Source changed during owner review; discarded the stale candidate.", ct); }
                catch (HttpRequestException error)
                {
                    lease = null;
                    await Log($"Development authorization unavailable; last installed app retained. The next edit requires fresh owner review: {error.Message}", ct);
                }
                catch (Exception error) when (error is not OperationCanceledException) { await Log($"Build/reload paused; last installed app retained: {error.Message}", ct); }
                if (renewal.IsFaulted) { await renewal; }
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await renewal; } catch (Exception error) { await Log($"Lease renewal stopped: {error.Message}", CancellationToken.None); }
            foreach (var token in leaseTokens)
            {
                using var revoke = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.Post<object>(client.Route + "/development/revoke", new { Token = token, Project = project }, revoke.Token); }
                catch (Exception error) { await Log($"Lease revoke unavailable; server expiry remains in force: {error.Message}", CancellationToken.None); }
            }
        }
    }
    private sealed class StaleCandidateException : Exception;
    internal static HttpClient Http(Uri? endpoint)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var http = new HttpClient(handler) { BaseAddress = endpoint ?? new Uri("http://localhost:5080/") };
        var cookie = Environment.GetEnvironmentVariable("INTOCHAT_SESSION_COOKIE");
        var bearer = Environment.GetEnvironmentVariable("INTOCHAT_BEARER_TOKEN");
        if (cookie is not null && bearer is not null) { http.Dispose(); throw new ArgumentException("Supply only one intentional session credential."); }
        try
        {
            if (cookie is not null)
            {
                // The container accepts replacement session cookies after an explicitly requested brain switch.
                foreach (var pair in cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                { handler.CookieContainer.SetCookies(http.BaseAddress, pair); }
            }
            if (bearer is not null) { http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer); }
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            http.Dispose();
            throw new ArgumentException("The intentional session credential is invalid.");
        }
        return http;
    }
}
