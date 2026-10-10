namespace DigitalBrain.Microsoft.Aspire.Cluster;

// The live AppHost, reached through the `aspire` CLI.
internal interface IClusterHost
{
    Task<string> CurrentSha(CancellationToken ct);                        // from {Workdir}/live.json
    Task SetLive(string sha, CancellationToken ct);                       // writes live.json (build must exist)
    Task<SiloView[]> Silos(CancellationToken ct);                         // `aspire describe`: the OperatingSystem-{x} instances
    Task RestartSilos(CancellationToken ct);                              // `aspire resource ... restart`: digitalbrain-mcp-*, then OperatingSystem-* (the calling silo last)
    Task RestartSilo(string name, CancellationToken ct);                  // `aspire resource {name} restart`: one instance, by name from Instances(); the calling silo's own restart is detached
    Task<SiloView> WaitSilo(string name, TimeSpan budget, CancellationToken ct); // Running and its own /ready answers 200, else TimeoutException; fails early when it exits
    Task<string[]> Instances(CancellationToken ct);                       // the roll order: digitalbrain-mcp-{x}, then OperatingSystem-{x}
    string? Self { get; }                                                 // the OperatingSystem-{x} instance this process is
    Task<int> Spans(string service, string span, CancellationToken ct);  // `aspire otel spans`: spans named `span` whose source is `service` or `service-*`
}
