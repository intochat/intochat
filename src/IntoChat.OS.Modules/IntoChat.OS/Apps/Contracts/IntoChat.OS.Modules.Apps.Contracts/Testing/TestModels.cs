using System;
using System.Collections.Generic;

namespace IntoChat.OS.Modules.Apps.Contracts.Testing;

[GenerateSerializer, Alias("testing.execution-request")]
public sealed record TestExecutionRequest(
    [property: Id(0)] string SuiteName,
    [property: Id(1)] string? Filter = null,
    [property: Id(2)] int TimeoutSeconds = 120,
    [property: Id(3)] string? Configuration = null);

[GenerateSerializer, Alias("testing.failure-detail")]
public sealed record TestFailureDetail(
    [property: Id(0)] string TestName,
    [property: Id(1)] string ErrorMessage,
    [property: Id(2)] string? StackTrace = null);

[GenerateSerializer, Alias("testing.execution-result")]
public sealed record TestExecutionResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] int Total,
    [property: Id(2)] int Passed,
    [property: Id(3)] int Failed,
    [property: Id(4)] int Skipped,
    [property: Id(5)] long DurationMs,
    [property: Id(6)] string Summary,
    [property: Id(7)] IReadOnlyList<TestFailureDetail> Failures);

[GenerateSerializer, Alias("testing.result-summary")]
public sealed record TestResultSummary(
    [property: Id(0)] string RunId,
    [property: Id(1)] string SuiteName,
    [property: Id(2)] DateTimeOffset ExecutedAt,
    [property: Id(3)] bool Success,
    [property: Id(4)] int Total,
    [property: Id(5)] int Passed,
    [property: Id(6)] int Failed,
    [property: Id(7)] long DurationMs);
