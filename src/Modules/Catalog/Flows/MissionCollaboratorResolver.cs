using System.Globalization;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal static class MissionCollaboratorResolver
{
    public static async Task<MissionCollaborator[]> ResolveAsync(
        BrainId brain,
        EngineerRun run,
        FlowStatus status,
        string currentStage,
        string ownerId,
        IServiceProvider? services = null,
        CancellationToken ct = default)
    {
        var logger = OsConsoleLog.For<EngineerFlow>();
        var list = new List<MissionCollaborator>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        var ownerDisplayName = string.IsNullOrWhiteSpace(ownerId) ? "Owner" : ownerId;
        Friend[] friends = [];

        if (services?.GetService<INeuronProxies>() is { } proxies)
        {
            try
            {
                var settings = await proxies.Get<IUserSettings>(new NeuronId(brain, "os.settings", "settings")).Read().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(settings.DisplayName) && !string.Equals(settings.DisplayName, "User", StringComparison.Ordinal))
                {
                    ownerDisplayName = settings.DisplayName;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "MissionCollaboratorResolver skipped IUserSettings lookup.");
            }

            try
            {
                var profile = await proxies.Get<IProfile>(new NeuronId(brain, "os.profile", "profile")).Read().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(profile.DisplayName))
                {
                    ownerDisplayName = profile.DisplayName;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "MissionCollaboratorResolver skipped IProfile lookup.");
            }

            try
            {
                friends = await proxies.Get<IFriends>(FriendsDefaults.Id(brain.Value)).List().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "MissionCollaboratorResolver skipped IFriends lookup.");
            }
        }

        var personFocus = status == FlowStatus.Waiting && run.RequirePlanRatification && run.RatifiedPlan is not { Ratified: true }
            ? "brainstorm-and-plan"
            : status == FlowStatus.Waiting
                ? "approval-gate"
                : currentStage;

        var normalizedOwnerId = string.IsNullOrWhiteSpace(ownerId) ? brain.Value : ownerId;
        if (seenIds.Add(normalizedOwnerId))
        {
            list.Add(new MissionCollaborator(
                Id: normalizedOwnerId,
                DisplayName: ownerDisplayName,
                Kind: "person",
                FocusNodeId: personFocus,
                Active: true));
        }

        // Resolve legacy linked circle owner when CircleBrainId is present on an older run.
        if (!string.IsNullOrWhiteSpace(run.CircleBrainId))
        {
            var circleOwners = await TryReadCircleOwnerAsync(run.CircleBrainId!, services, logger, ct).ConfigureAwait(false);
            foreach (var (memberId, memberDisplay) in circleOwners)
            {
                if (seenIds.Add(memberId))
                {
                    list.Add(new MissionCollaborator(
                        Id: memberId,
                        DisplayName: memberDisplay,
                        Kind: "friend",
                        FocusNodeId: currentStage,
                        Active: true));
                }
            }
        }

        foreach (var friend in friends)
        {
            var friendId = friend.Account.Value;
            if (!string.IsNullOrWhiteSpace(friendId) && seenIds.Add(friendId))
            {
                list.Add(new MissionCollaborator(
                    Id: friendId,
                    DisplayName: string.IsNullOrWhiteSpace(friend.Name) ? friendId : friend.Name,
                    Kind: "friend",
                    FocusNodeId: currentStage,
                    Active: true));
            }
        }

        // Resolve active Ino slice workers or external CLI worker.
        var selectedLeaseId = run.SelectedInstance?.Lease?.EffectiveLeaseId
            ?? string.Create(CultureInfo.InvariantCulture, $"wt-{run.SelectedCandidateIndex}");

        if (run.Harness is EngineeringHarnessKind.ClaudeCli or EngineeringHarnessKind.CodexCli)
        {
            var cliId = run.Harness == EngineeringHarnessKind.ClaudeCli ? "cli.claude" : "cli.codex";
            var cliLabel = run.Harness == EngineeringHarnessKind.ClaudeCli ? "Claude CLI" : "Codex CLI";
            if (seenIds.Add(cliId))
            {
                list.Add(new MissionCollaborator(
                    Id: cliId,
                    DisplayName: cliLabel,
                    Kind: "cli-agent",
                    FocusNodeId: selectedLeaseId,
                    Active: status == FlowStatus.Running && string.Equals(currentStage, "implement-tasks", StringComparison.Ordinal)));
            }
        }
        else
        {
            var slices = run.SelectedInstance?.Slices
                ?? run.RatifiedPlan?.Slices
                ?? (run.Plan is not null ? EngineerTaskSlices.FromTaskPlan(run.Plan) : []);

            if (slices.Length == 0)
            {
                if (seenIds.Add("ino.slice-1"))
                {
                    list.Add(new MissionCollaborator(
                        Id: "ino.slice-1",
                        DisplayName: "Ino · Slice 1",
                        Kind: "ino-agent",
                        FocusNodeId: $"{selectedLeaseId}:slice-1",
                        Active: status == FlowStatus.Running));
                }
            }
            else
            {
                foreach (var slice in slices)
                {
                    var agentKey = string.IsNullOrWhiteSpace(slice.AssignedAgentKey) ? $"ino.{slice.SliceId}" : slice.AssignedAgentKey;
                    if (!seenIds.Add(agentKey))
                    {
                        continue;
                    }

                    var isSliceActive = status == FlowStatus.Running
                        && string.Equals(currentStage, "implement-tasks", StringComparison.Ordinal)
                        && !string.Equals(slice.Status, "completed", StringComparison.OrdinalIgnoreCase);

                    list.Add(new MissionCollaborator(
                        Id: agentKey,
                        DisplayName: $"Ino · {slice.Title}",
                        Kind: "ino-agent",
                        FocusNodeId: $"{selectedLeaseId}:{slice.SliceId}",
                        Active: isSliceActive));
                }
            }
        }

        return [.. list];
    }

    private static async Task<IReadOnlyList<(string Id, string DisplayName)>> TryReadCircleOwnerAsync(
        string circleBrainId,
        IServiceProvider? services,
        ILogger logger,
        CancellationToken ct)
    {
        var results = new List<(string Id, string DisplayName)>();
        var broker = services?.GetService<ICapabilityBroker>();
        if (broker is null)
        {
            return results;
        }

        string[] memberAccountIds = [];
        try
        {
            var identity = await broker.Identity(ct).ConfigureAwait(false);
            if (await identity.OwnerOfBrain(circleBrainId, ct).ConfigureAwait(false) is { } circleOwner)
            {
                memberAccountIds = [circleOwner.AccountId];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "MissionCollaboratorResolver could not read circle owner for '{CircleBrain}'.", circleBrainId);
        }

        foreach (var accountId in memberAccountIds.Where(static m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal))
        {
            var displayName = accountId;
            if (broker is not null)
            {
                try
                {
                    var identity = await broker.Identity(ct).ConfigureAwait(false);
                    if (await identity.ReadAccount(accountId, ct).ConfigureAwait(false) is { Name.Length: > 0 } acct)
                    {
                        displayName = acct.Name;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "MissionCollaboratorResolver skipped ReadAccount for '{AccountId}'.", accountId);
                }
            }

            results.Add((accountId, displayName));
        }

        return results;
    }
}
