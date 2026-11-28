using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Handles;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Settings;

public sealed partial class SettingsApp
{
    public const string FriendGrantsVariable = "settings.friend-connection-grants";

    [On]
    public async Task MintFriendCode(SettingsMintFriendCode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Friends is not { } friends)
        {
            await UpdateAsync(State with { Section = "team", TeamError = "Friends directory is unavailable." });
            return;
        }

        try
        {
            var code = await friends.Mint();
            var roster = await TryReadTeamRosterAsync();
            var expiresAt = Context.Time.GetUtcNow().AddHours(24);
            await UpdateAsync(State with
            {
                Section = "team",
                ActiveFriendCode = code.Value,
                ActiveFriendCodeExpiresAt = expiresAt,
                TeamRoster = roster,
                TeamError = null,
                Status = "Minted 24h friend code.",
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogWarning(error, "Failed to mint 24h friend code.");
            await UpdateAsync(State with { Section = "team", TeamError = error.Message });
        }
    }

    [On]
    public async Task RedeemFriendCode(SettingsRedeemFriendCode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = request.Code?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            await PublishEphemeral(new Rejected("redeem-friend-code", ["invalid_code"], ["Enter a valid 24h friend code to redeem."]));
            return;
        }

        if (Friends is not { } friends)
        {
            await UpdateAsync(State with { Section = "team", TeamError = "Friends directory is unavailable." });
            return;
        }

        try
        {
            var added = await friends.Redeem(new Code(raw));
            var roster = await TryReadTeamRosterAsync();
            var label = string.IsNullOrWhiteSpace(added.Name) ? $"@{added.Account.Value}" : added.Name;
            await UpdateAsync(State with
            {
                Section = "team",
                TeamRoster = roster,
                TeamError = null,
                Status = $"Connected with {label}.",
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogWarning(error, "Failed to redeem friend code.");
            await UpdateAsync(State with { Section = "team", TeamError = error.Message });
        }
    }

    [On]
    public async Task Unfriend(SettingsUnfriend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var account = request.Account?.Trim();
        if (string.IsNullOrWhiteSpace(account))
        {
            await PublishEphemeral(new Rejected("unfriend", ["invalid_account"], ["Specify an account to remove."]));
            return;
        }

        if (Friends is not { } friends)
        {
            await UpdateAsync(State with { Section = "team", TeamError = "Friends directory is unavailable." });
            return;
        }

        try
        {
            await friends.Unfriend(new AccountId(account));
            var roster = await TryReadTeamRosterAsync();
            var prefix = account + ":";
            var remainingGrants = (State.FriendConnectionGrants ?? [])
                .Where(g => !g.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            await TryPersistFriendGrantsAsync(remainingGrants);
            await UpdateAsync(State with
            {
                Section = "team",
                SubPage = string.Equals(State.SubPage, account, StringComparison.OrdinalIgnoreCase) ? null : State.SubPage,
                TeamRoster = roster,
                FriendConnectionGrants = remainingGrants,
                TeamError = null,
                Status = $"Removed {account} from People & Team.",
            });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogWarning(error, "Failed to unfriend account {Account}.", account);
            await UpdateAsync(State with { Section = "team", TeamError = error.Message });
        }
    }

    [On]
    public async Task LookupHandle(SettingsLookupHandle request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = request.Handle?.Trim().TrimStart('@').Trim();
        if (string.IsNullOrWhiteSpace(raw) || !IHandleDirectory.IsValid(raw.ToLowerInvariant()))
        {
            await UpdateAsync(State with
            {
                Section = "team",
                HandleLookupResult = null,
                TeamError = "Handles must be 3-32 lowercase letters, digits, or hyphens.",
            });
            return;
        }

        var normalized = IHandleDirectory.Normalize(raw);
        string? resolvedBrain = null;
        try
        {
            var handleId = IHandleDirectory.IdOf(normalized);
            resolvedBrain = await Handles[handleId].Resolve();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Handle directory lookup unavailable for @{Handle}; falling back to roster.", normalized);
        }

        var roster = State.TeamRoster ?? await TryReadTeamRosterAsync();
        var rosterMatch = (roster ?? []).FirstOrDefault(f =>
            string.Equals(f.Account.Value, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.Name, normalized, StringComparison.OrdinalIgnoreCase));

        var result = resolvedBrain is { Length: > 0 }
            ? $"@{normalized} · brain {resolvedBrain}"
            : rosterMatch is not null
                ? $"@{normalized} · friend ({rosterMatch.Brain.Value})"
                : $"@{normalized} not found";

        await UpdateAsync(State with
        {
            Section = "team",
            HandleLookupResult = result,
            TeamError = null,
            Status = result,
        });
    }

    [On]
    public async Task SetFriendPermission(SettingsSetFriendPermission request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var account = request.Account?.Trim();
        var permission = request.Permission?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(permission))
        {
            await PublishEphemeral(new Rejected("set-friend-permission", ["invalid_permission"], ["Specify an account and permission to update."]));
            return;
        }

        var token = $"{account}:{permission}";
        var current = State.FriendConnectionGrants ?? [];
        var nextGrants = request.Allowed
            ? current.Contains(token, StringComparer.OrdinalIgnoreCase) ? current : [.. current, token]
            : current.Where(g => !string.Equals(g, token, StringComparison.OrdinalIgnoreCase)).ToArray();

        await TryPersistFriendGrantsAsync(nextGrants);
        await UpdateAsync(State with
        {
            Section = "team",
            FriendConnectionGrants = nextGrants,
            TeamError = null,
            Status = $"{(request.Allowed ? "Granted" : "Revoked")} '{permission}' for {account}.",
        });
    }

    private async Task TryPersistFriendGrantsAsync(string[] grants)
    {
        try
        {
            await UserSettings.SetVariable(FriendGrantsVariable, string.Join(',', grants));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "Skipping friend connection grants persistence in minimal test host.");
        }
    }
}
