namespace DigitalBrain.Capabilities.Identity;

using DigitalBrain.Access;
using DigitalBrain.Capabilities;

/// <summary>
/// Capability binding for <see cref="ISessions"/> and <see cref="IIdentityProvider"/> over an <see cref="IIdentitySessionPort"/>,
/// reading the stamped caller from an <see cref="ICallerAmbient"/>.
/// </summary>
public sealed class BoundSessions : ISessions, IIdentityProvider
{
    private readonly IIdentitySessionPort _port;
    private readonly Action<string?, CapabilityId, string>? _requirePrivileged;
    private readonly ICallerAmbient _callers;

    /// <summary>Creates a new instance of <see cref="BoundSessions"/>.</summary>
    /// <param name="port">The underlying session port.</param>
    /// <param name="callers">The ambient the stamped caller is read from.</param>
    /// <param name="requirePrivileged">Optional callback validating privileged callers; it receives the id of the calling module or the key of the calling app, and null for any other caller.</param>
    public BoundSessions(IIdentitySessionPort port, ICallerAmbient callers, Action<string?, CapabilityId, string>? requirePrivileged = null)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(callers);
        _port = port;
        _callers = callers;
        _requirePrivileged = requirePrivileged;
    }

    /// <inheritdoc />
    public Task<SessionIssued> Issue(SessionRequest request, CancellationToken cancellationToken = default)
    {
        Privileged(nameof(Issue));
        SessionClaims.Validate(request);
        return _port.Issue(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task End(string sessionId, CancellationToken cancellationToken = default)
    {
        Privileged(nameof(End));
        ArgumentNullException.ThrowIfNull(sessionId);
        return _port.End(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Member?> Validate(string sessionId, CancellationToken cancellationToken = default)
    {
        Privileged(nameof(Validate));
        ArgumentNullException.ThrowIfNull(sessionId);
        return _port.Validate(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public IdentityProviderDescriptor Describe() => _port.Describe();

    private void Privileged(string member)
    {
        var caller = _callers.Current ?? throw new UnauthorizedAccessException("identity@1: the call carries no caller.");
        var id = caller.Actor switch
        {
            Actor.Module module => module.Id,
            Actor.App app => app.Key,
            _ => null,
        };
        _requirePrivileged?.Invoke(id, Capability.Identity.V1, member);
    }
}
