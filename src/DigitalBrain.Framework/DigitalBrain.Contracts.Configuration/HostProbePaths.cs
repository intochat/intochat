namespace DigitalBrain.Framework;

// The probes every DigitalBrain host answers (HealthProbes maps them): /health with dependencies,
// /ready the process plus the drain, /alive the process alone.
public static class HostProbePaths
{
    public const string Health = "/health";
    public const string Ready = "/ready";
    public const string Alive = "/alive";
}
