namespace IntoChat.OS.Modules.Devices;

/// <summary>
/// Live runtime consent store for this device's explicit <c>AllowClientCodeSandbox</c> toggle (RFC-2).
/// Falls back to platform-aware <see cref="ComputeCapabilityOptions"/> defaults (Desktop: ON, Mobile: OFF)
/// until explicitly toggled via Settings or <see cref="Link.IDeviceLink"/>.
/// </summary>
internal sealed class ClientCodeSandboxConsentStore : IClientCodeSandboxConsent, IClientResourceConsent
{
    private readonly Lock _gate = new();
    private bool _allowClientCodeSandbox;
    private bool _hasExplicitOverride;
    private bool _allowWindowsFolderGateway;
    private bool _allowConsole;

    public ClientCodeSandboxConsentStore(bool initialAllow)
    {
        _allowClientCodeSandbox = initialAllow;
    }

    public ClientCodeSandboxConsentStore()
        : this(ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform())
    {
    }

    public event Action<bool>? Changed;

    private event Action? ResourceChanged;

    event Action? IClientResourceConsent.Changed
    {
        add => ResourceChanged += value;
        remove => ResourceChanged -= value;
    }

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

    public bool AllowWindowsFolderGateway
    {
        get
        {
            lock (_gate)
            {
                return _allowWindowsFolderGateway;
            }
        }
    }

    public bool AllowConsole
    {
        get
        {
            lock (_gate)
            {
                return _allowConsole;
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
            ResourceChanged?.Invoke();
        }
    }

    public void SetAllowWindowsFolderGateway(bool allow)
    {
        bool changed;
        lock (_gate)
        {
            changed = _allowWindowsFolderGateway != allow;
            _allowWindowsFolderGateway = allow;
        }

        if (changed)
        {
            ResourceChanged?.Invoke();
        }
    }

    public void SetAllowConsole(bool allow)
    {
        bool changed;
        lock (_gate)
        {
            changed = _allowConsole != allow;
            _allowConsole = allow;
        }

        if (changed)
        {
            ResourceChanged?.Invoke();
        }
    }
}
