namespace IntoChat.OS.Kernel.Tests.E2E.Aspire.Journeys;

public sealed record Journey(
    string App,
    string Operation,
    string Argument,
    string FixtureFile);
