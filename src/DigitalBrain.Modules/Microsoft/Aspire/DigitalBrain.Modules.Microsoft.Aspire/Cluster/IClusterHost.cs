namespace DigitalBrain.Microsoft.Aspire.Cluster;

// The live AppHost, reached through the `aspire` CLI.
internal interface IClusterHost
{
    Task<string> CurrentSha(CancellationToken ct);                        // from {Workdir}/live.json
    Task SetLive(string sha, CancellationToken ct);                       // writes live.json (build must exist)
    Task<SiloView[]> Silos(CancellationToken ct);                         // `aspire describe`: the OperatingSystem replicas
    Task RestartSilos(CancellationToken ct);                              // `aspire resource ... restart`: digitalbrain-mcp, then the silo (which runs this call)
    Task<int> Spans(string service, string span, CancellationToken ct);  // `aspire otel spans`: spans named `span` whose source is `service` or `service-*`
}
