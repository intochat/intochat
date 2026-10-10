namespace DigitalBrain.Microsoft.Aspire.Cluster;

// The live AppHost, reached through the `aspire` CLI.
internal interface IClusterHost
{
    Task<string> CurrentSha(CancellationToken ct);                        // from {Workdir}/live.json
    Task SetLive(string sha, CancellationToken ct);                       // writes live.json (build must exist)
    Task<SiloView[]> Silos(CancellationToken ct);                         // list_resources filtered to silo replicas
    Task RestartSilos(CancellationToken ct);                              // execute_resource_command restart on the silo resource
    Task<int> Spans(string service, string span, CancellationToken ct);  // list_traces: spans named `span` exported by `service`
}
