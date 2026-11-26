namespace IntoChat.OS.Modules.Devices;

/// <summary>
/// Live runtime consent store for this device's explicit <c>AllowClientCodeSandbox</c> toggle (RFC-2).
/// Falls back to platform-aware <see cref="ComputeCapabilityOptions"/> defaults (Desktop: ON, Mobile: OFF)
/// until explicitly toggled via Settings or <see cref="Link.IDeviceLink"/>.
/// </summary>
internal sealed class ClientCodeSandboxConsentStore : IClientCodeSandboxConsent
{
    private readonly Lock _gate = new();
    private bool _allowClientCodeSandbox;
    private bool _hasExplicitOverride;

    public ClientCodeSandboxConsentStore(bool initialAllow)
    {
        _allowClientCodeSandbox = initialAllow;
    }

    public ClientCodeSandboxConsentStore()
        : this(ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform())
    {
    }

    public event Action<bool>? Changed;

    public bool AllowClientCodeSandbox
    {
        get
        {
            lock (_gate)
            {
                return _allowClientCodeSandbox;
            }
        }
    }

    public bool HasExplicitOverride
    {
        get
        {
            lock (_gate)
            {
                return _hasExplicitOverride;
            }
        }
    }

    public bool Resolve(bool optionsDefault)
    {
        lock (_gate)
        {
            return _hasExplicitOverride ? _allowClientCodeSandbox : optionsDefault;
        }
    }

    public void Set(bool allow)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_hasExplicitOverride || _allowClientCodeSandbox != allow;
            _allowClientCodeSandbox = allow;
            _hasExplicitOverride = true;
        }

        if (changed)
        {
            Changed?.Invoke(allow);
        }
    }
}
