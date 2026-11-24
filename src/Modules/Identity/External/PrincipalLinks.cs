using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Directory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Identity.External;

// One entry of a principal-links file: a person the cloud signed up into this install's directory before the cloud/OS split, by the
// address the cloud vouches for and the principal it registered then.
public sealed record PrincipalLink(string Issuer, string Email, string Principal);

// What applying one entry did. Outcome is "linked", "already-linked", "no-such-principal", "conflict", "untrusted-issuer" or
// "invalid-email"; the address itself is not repeated.
public sealed record PrincipalLinkOutcome(string Principal, string Linked, string Outcome);

// The mapping step of the cloud/OS split (step 6): after it, a person the cloud provisioned into this install before the split signs
// in through the issuer's external ticket (ExternalSignIn) to the account and brains they already have, instead of a new one. Each
// entry links the issuer-scoped principal (ExternalSignIn.PrincipalOf) to the existing principal's default membership and brains.
// It only adds rows: it never changes or removes the existing principal, and a linked principal that already signs in elsewhere is
// reported as a conflict and left alone. Idempotent: a second run reports "already-linked".
//
// Operator: set IntoChatOS:Auth:PrincipalLinks to the file's path for one start; the result is written beside it as
// "<file>.result.json"; then remove the setting.
public sealed class PrincipalLinks
{
    private readonly AuthorityNeurons neurons;
    private readonly EdgeAuthority edge;
    private readonly IOptions<AuthOptions> auth;
    private readonly ILogger<PrincipalLinks> log;

    internal PrincipalLinks(AuthorityNeurons neurons, EdgeAuthority edge, IOptions<AuthOptions> auth, ILogger<PrincipalLinks> log)
    {
        this.neurons = neurons;
        this.edge = edge;
        this.auth = auth;
        this.log = log;
    }

    public Task Execute(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(auth.Value.PrincipalLinks) ? Task.CompletedTask : ApplyFileAsync(auth.Value.PrincipalLinks, cancellationToken);

    public async Task<PrincipalLinkOutcome[]> ApplyFileAsync(string file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        PrincipalLink[] links;
        try
        {
            links = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(file, cancellationToken), PrincipalLinksJson.Default.PrincipalLinkArray) ?? [];
        }
        catch (Exception problem) when (problem is IOException or JsonException or UnauthorizedAccessException)
        {
            // The install still starts: a mapping file the operator got wrong must not take the OS down.
            log.LogCritical(problem, "Principal links file {File} could not be read; no principal was linked", file);
            return [];
        }
        var outcomes = await ApplyAsync(links, cancellationToken);
        await File.WriteAllBytesAsync(file + ".result.json", JsonSerializer.SerializeToUtf8Bytes(outcomes, PrincipalLinksJson.Default.PrincipalLinkOutcomeArray), cancellationToken);
        log.LogWarning("Principal links from {File}: {Linked} linked, {Already} already linked, {Other} not linked (see {Result})", file,
            outcomes.Count(o => o.Outcome == "linked"), outcomes.Count(o => o.Outcome == "already-linked"),
            outcomes.Count(o => o.Outcome is not ("linked" or "already-linked")), file + ".result.json");
        return outcomes;
    }

    public async Task<PrincipalLinkOutcome[]> ApplyAsync(IReadOnlyList<PrincipalLink> links, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(links);
        var trusted = auth.Value.TrustedIssuers.Select(issuer => issuer.Name).ToHashSet(StringComparer.Ordinal);
        var outcomes = new List<PrincipalLinkOutcome>(links.Count);
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!trusted.Contains(link.Issuer ?? "")) { outcomes.Add(new(link.Principal ?? "", "", "untrusted-issuer")); continue; }
            if (ExternalSignInTicket.NormalizeEmail(link.Email) is not { } email || string.IsNullOrWhiteSpace(link.Principal))
            { outcomes.Add(new(link.Principal ?? "", "", "invalid-email")); continue; }
            var linked = ExternalSignIn.PrincipalOf(link.Issuer!, email);
            PrincipalLinkResult result;
            // The directory's own module identity, as every directory write of this module (LocalIdentityClient).
            using (edge.Enter(edge.Module(IdentityDirectoryGrain.DirectoryModuleId, new BrainId("system"))))
            {
                result = await neurons.Directory().LinkPrincipal(link.Principal, linked).WaitAsync(cancellationToken);
            }
            outcomes.Add(new(link.Principal, linked, result switch
            {
                PrincipalLinkResult.Linked => "linked",
                PrincipalLinkResult.AlreadyLinked => "already-linked",
                PrincipalLinkResult.NoSuchPrincipal => "no-such-principal",
                _ => "conflict",
            }));
        }
        return [.. outcomes];
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(PrincipalLink[]))]
[JsonSerializable(typeof(PrincipalLinkOutcome[]))]
internal sealed partial class PrincipalLinksJson : JsonSerializerContext;
