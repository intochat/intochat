namespace IntoChat.OS.Sdk.Modules;

public enum ModuleState { Active, Unlicensed, Disabled }

public sealed record ModuleStatus(ModuleId Module, ModuleState State, string? Reason);
