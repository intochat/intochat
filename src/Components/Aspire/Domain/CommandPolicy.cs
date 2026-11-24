namespace IntoChat.Aspire.Domain;

internal static class RefusalReasons
{
    public const string Unavailable = "unavailable";
    public const string Unknown = "unknown";
    public const string UnknownCommand = "unknown-command";
    public const string Self = "self";
    public const string PersonOnly = "person-only";
    public const string Pending = "pending";
    public const string Declined = "declined";
    public const string Failed = "failed";
}

internal abstract record CommandDecision;

internal sealed record AllowCommandNow(string ModelName, AspireInstanceView Instance, AspireCommand Command) : CommandDecision;

internal sealed record RequireCommandApproval(
    string ModelName,
    AspireInstanceView Instance,
    AspireCommand Command,
    string CardTitle,
    string CardBody) : CommandDecision;

internal sealed record RefuseCommand(
    string Resource,
    string Command,
    string Reason,
    string? Message = null,
    string[]? Known = null) : CommandDecision
{
    public AspireCommandResult ToResult(string? confirmation = null)
        => new(Resource, Command, false, Reason, Message, confirmation, Known);
}

// Pure stateless domain policy for executing commands on live resources.
// Called identically on initial Execute and on confirmation approval so rules never drift.
internal static class CommandPolicy
{
    public static CommandDecision Evaluate(
        RuntimeTopology topology,
        bool isDirectPerson,
        string? target,
        string? commandName,
        string? selfKernelName,
        string[]? personOnlyCommands)
    {
        var requestedResource = target?.Trim() ?? "";
        var requestedCommand = commandName?.Trim() ?? "";

        // 1. Bridge connectivity
        if (!topology.Connected)
        {
            return new RefuseCommand(requestedResource, requestedCommand, RefusalReasons.Unavailable);
        }

        // 2. Target resolution (instance id or single-instance model name)
        var resolution = topology.Resolve(requestedResource);
        if (resolution is AmbiguousTarget ambiguous)
        {
            return new RefuseCommand(requestedResource, requestedCommand, RefusalReasons.Unknown, Known: ambiguous.ReplicaIds);
        }
        if (resolution is UnknownTarget unknown)
        {
            return new RefuseCommand(requestedResource, requestedCommand, RefusalReasons.Unknown, Known: unknown.KnownModelNames);
        }

        var resolved = (ResolvedTarget)resolution;
        var instance = resolved.Instance;

        // 3. Command must be listed on this instance and enabled
        var command = instance.Commands.FirstOrDefault(c =>
            c.Enabled && string.Equals(c.Name, requestedCommand, StringComparison.Ordinal));
        if (command is null)
        {
            var available = instance.Commands.Select(c => c.Name).ToArray();
            return new RefuseCommand(instance.Id, requestedCommand, RefusalReasons.UnknownCommand, Known: available);
        }

        // 4. Kernel self-protection (every replica, front, rebuilder)
        if (KernelNames.IsSelfLive(resolved.ModelName, instance.Id, selfKernelName))
        {
            return new RefuseCommand(instance.Id, command.Name, RefusalReasons.Self);
        }

        // 5. Person-only deny-list for non-person callers
        if (!isDirectPerson
            && personOnlyCommands is { Length: > 0 }
            && personOnlyCommands.Any(denied => string.Equals(denied, command.Name, StringComparison.Ordinal)))
        {
            return new RefuseCommand(instance.Id, command.Name, RefusalReasons.PersonOnly);
        }

        // 6. Direct person runs immediately
        if (isDirectPerson)
        {
            return new AllowCommandNow(resolved.ModelName, instance, command);
        }

        // 7. Non-person requires explicit person approval
        var displayName = string.IsNullOrWhiteSpace(command.DisplayName) ? command.Name : command.DisplayName;
        var title = $"{displayName} on {instance.Id}?";
        var body = !string.IsNullOrWhiteSpace(command.ConfirmationMessage)
            ? command.ConfirmationMessage
            : $"Run {displayName} on the resource {instance.Id} of the running app.";
        return new RequireCommandApproval(resolved.ModelName, instance, command, title, body);
    }
}
