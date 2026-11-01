using System.Collections.Generic;
using System.Threading.Tasks;

namespace IntoChat.OS.Modules.Apps.Contracts.Testing;

[DigitalBrain.Contracts.NeuronAlias("testing.verification"), DigitalBrain.Contracts.DefaultNeuronType("testing.verification")]
public interface ITestVerificationNeuron
{
    Task<TestExecutionResult> RunTargetedSuiteAsync(TestExecutionRequest request);
    Task<IReadOnlyList<TestResultSummary>> GetHistoricalRunsAsync(string testSuiteId);
}
