using DigitalBrain;

namespace DigitalBrain.Apps;

[GenerateSerializer, Alias("intochat.app-drafts-changed")]
public sealed record AppDraftsChanged([property: Id(0)] string Owner) : Signal;

