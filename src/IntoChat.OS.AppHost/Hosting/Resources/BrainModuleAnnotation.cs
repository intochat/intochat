using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting;

public sealed record BrainModuleAnnotation(string ModuleId) : IResourceAnnotation;
