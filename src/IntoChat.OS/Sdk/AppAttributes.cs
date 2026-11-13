namespace IntoChat.Sdk;

/// <summary>Declares an IntoChat app hosted by the sandbox runtime.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AppAttribute(string key) : Attribute
{
    public string Key { get; } = key;
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public string Icon { get; init; } = "apps";
    public string Category { get; init; } = "Automation";
    public string Author { get; init; } = string.Empty;
}

/// <summary>Subscribes a handler method to a brain signal by contract name (e.g. "windows.file-changed").</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class OnSignalAttribute(string signal) : Attribute
{
    public string Signal { get; } = signal;
}

/// <summary>Schedules a handler method using a cadence expression ("@hourly", "5m", or 5-field cron).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class EveryAttribute(string cron) : Attribute
{
    public string Cron { get; } = cron;
}

/// <summary>Exposes a command handler callable from the OS UI, chat agents, or MCP.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class OnCommandAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; init; } = string.Empty;
}

/// <summary>Declares a typed capability slot required or optionally used by the app.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class SlotAttribute(string name, Type contract) : Attribute
{
    public string Name { get; } = name;
    public Type Contract { get; } = contract;
    public bool Required { get; init; } = true;
    public string Description { get; init; } = string.Empty;
    public FolderAccess Access { get; init; } = FolderAccess.Read;
    public string DefaultPath { get; init; } = string.Empty;
}
