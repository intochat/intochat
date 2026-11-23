namespace IntoChat.OS.Modules.Apps;

// Confirmations buttons are an OS-surface privilege, decided in two steps. (1) Formatting hint: AppPresentation formats a
// confirmations button only for an app whose settings name a compiled type (IsOsTrusted here); any other app's is inert.
// (2) Authority: AppUiEndpoints.Deliver resolves a confirmation only when IHostedAppTypes says the host itself loaded the
// app's type from a registered compiled-app assembly. A setting is never authority on its own. The stored assembly-qualified name
// need not resolve for the unedited shipped copy of a host-loaded app (AppNeuron.VerifyCompiledProgramType); other records must resolve it.
public static class AppTrust
{
    public const string CompiledTypeSetting = "__CompiledType";

    public static bool IsOsTrusted(IReadOnlyDictionary<string, string>? settings)
        => settings is not null && settings.TryGetValue(CompiledTypeSetting, out var type) && !string.IsNullOrWhiteSpace(type);

    public static bool IsOsTrusted(AppSnapshot? app) => app is not null && IsOsTrusted(app.Settings);
}
