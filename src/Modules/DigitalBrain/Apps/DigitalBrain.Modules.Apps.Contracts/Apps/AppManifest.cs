namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("apps.operation2")]
public sealed record AppOperation([property: Id(0)] string Name, [property: Id(1)] string Description);

[GenerateSerializer, Alias("apps.setting2")]
public sealed record AppSetting([property: Id(0)] string Name, [property: Id(1)] string Description, [property: Id(2)] string Default);

// A named credential slot ("gmail"); the bound connection id is per-brain state, never shared.
[GenerateSerializer, Alias("apps.slot")]
public sealed record AppConnectionSlot([property: Id(0)] string Name, [property: Id(1)] string Connector);

[GenerateSerializer, Alias("apps.manifest2")]
public sealed record AppManifest(
    [property: Id(0)] string Title,
    [property: Id(1)] string Description,
    [property: Id(2)] AppOperation[] Operations,
    [property: Id(3)] AppSetting[] Settings,
    [property: Id(4)] AppConnectionSlot[] Slots);
