namespace IntoChat.OS.Modules.Devices;

/// <summary>
/// Live runtime consent store for this device's explicit <c>AllowClientCodeSandbox</c> toggle (RFC-2).
/// Falls back to platform-aware <see cref="ComputeCapabilityOptions"/> defaults (Desktop: ON, Mobile: OFF)
/// until explicitly toggled via Settings or <see cref="Link.IDeviceLink"/>.
/// </summary>
internal sealed class ClientCodeSandboxConsentStore : IClientCodeSandboxConsent, IClientResourceConsent, IClientEngineeringConsent
{
    private readonly Lock _gate = new();
    private bool _allowClientCodeSandbox;
    private bool _hasExplicitOverride;
    private bool _allowWindowsFolderGateway;
    private bool _allowConsole;
    private bool _allowConnectedEngineering;

    public ClientCodeSandboxConsentStore(bool initialAllow, bool initialAllowConnectedEngineering = false)
    {
        _allowClientCodeSandbox = initialAllow;
        _allowConnectedEngineering = initialAllowConnectedEngineering;
    }

    public ClientCodeSandboxConsentStore()
        : this(
            ClientComputeConsentPolicy.DefaultAllowClientCodeSandboxForCurrentPlatform(),
            ClientComputeConsentPolicy.DefaultAllowConnectedEngineeringForCurrentPlatform())
    {
    }

    public event Action<bool>? Changed;

    private event Action? ResourceChanged;

    private event Action<bool>? EngineeringChanged;

    event Action? IClientResourceConsent.Changed
    {
        add => ResourceChanged += value;
        remove => ResourceChanged -= value;
    }

    event Action<bool>? IClientEngineeringConsent.Changed
    {
        add => EngineeringChanged += value;
        remove => EngineeringChanged -= value;
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

    public bool AllowConnectedEngineering
    {
        get
        {
            lock (_gate)
            {
                return _allowConnectedEngineering;
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

    public void SetAllowConnectedEngineering(bool allow)
    {
        bool changed;
        lock (_gate)
        {
            changed = _allowConnectedEngineering != allow;
            _allowConnectedEngineering = allow;
        }

        if (changed)
        {
            EngineeringChanged?.Invoke(allow);
            ResourceChanged?.Invoke();
        }
    }
}
