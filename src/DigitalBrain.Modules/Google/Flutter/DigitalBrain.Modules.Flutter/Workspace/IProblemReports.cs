using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Flutter.Workspace;

[Alias("ui.problem-reports"), Orleans.Metadata.DefaultGrainType("ui.problem-reports")]
internal interface IProblemReports : INeuron
{
    Task<ProblemReport> Add(string workspaceId, string intentId, string message);
    Task<IReadOnlyList<ProblemReport>> Read();
}
