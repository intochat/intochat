using System.Globalization;
using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Modules.Apps.Flows;

public sealed record LoopBudget(
    int RunsPerDay = 10,
    long TokensPerRun = 100_000,
    int AttemptsPerFinding = 3,
    bool KillSwitch = false);

public sealed class LoopBudgetExceededException(string code, string message) : Exception(message)
{
    public const string KillSwitchCode = "loop.kill-switch";
    public const string DailyBudgetCode = "loop.budget.daily";
    public const string AttemptsBudgetCode = "loop.budget.attempts";
    public const string TokensBudgetCode = "loop.budget.tokens";

    public string Code { get; } = code;
}

public sealed class LoopBudgetTracker
{
    private readonly LoopBudget _budget;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _failuresByFinding = new(StringComparer.Ordinal);
    private readonly HashSet<string> _parkedFindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _tokensByRun = new(StringComparer.Ordinal);
    private DateOnly _currentDay;
    private int _runsToday;

    public LoopBudgetTracker(LoopBudget budget, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        _budget = budget;
        _time = time ?? TimeProvider.System;
        _currentDay = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
    }

    public void EnsureNotKilled()
    {
        if (_budget.KillSwitch)
        {
            throw new LoopBudgetExceededException(
                LoopBudgetExceededException.KillSwitchCode,
                "Autonomous loop execution is disabled by the kill switch.");
        }
    }

    public void EnsureCanStartRun(string findingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingKey);

        lock (_gate)
        {
            EnsureCanStartRunLocked(findingKey);
        }
    }

    public void RecordRunStarted(string findingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingKey);

        lock (_gate)
        {
            EnsureCanStartRunLocked(findingKey);
            _runsToday++;
        }
    }

    public void RecordFindingFailed(string findingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingKey);

        lock (_gate)
        {
            var failures = _failuresByFinding.GetValueOrDefault(findingKey) + 1;
            _failuresByFinding[findingKey] = failures;
            if (failures >= _budget.AttemptsPerFinding)
            {
                _parkedFindings.Add(findingKey);
            }
        }
    }

    public bool IsParked(string findingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingKey);

        lock (_gate)
        {
            return _parkedFindings.Contains(findingKey)
                || (_failuresByFinding.TryGetValue(findingKey, out var failures) && failures >= _budget.AttemptsPerFinding);
        }
    }

    public long RecordTokens(string runId, InferenceUsage? usage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        EnsureNotKilled();

        var delta = usage is null
            ? 0L
            : usage.TotalTokens ?? ((usage.InputTokens ?? 0L) + (usage.OutputTokens ?? 0L));
        if (delta < 0)
        {
            delta = 0;
        }

        lock (_gate)
        {
            var total = _tokensByRun.GetValueOrDefault(runId) + delta;
            _tokensByRun[runId] = total;
            if (total > _budget.TokensPerRun)
            {
                throw new LoopBudgetExceededException(
                    LoopBudgetExceededException.TokensBudgetCode,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Run '{runId}' exceeded the per-run token budget ({total} > {_budget.TokensPerRun})."));
            }

            return total;
        }
    }

    private void EnsureCanStartRunLocked(string findingKey)
    {
        EnsureNotKilled();

        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        if (today != _currentDay)
        {
            _currentDay = today;
            _runsToday = 0;
        }

        if (_runsToday >= _budget.RunsPerDay)
        {
            throw new LoopBudgetExceededException(
                LoopBudgetExceededException.DailyBudgetCode,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Daily autonomous loop run cap reached ({_runsToday}/{_budget.RunsPerDay})."));
        }

        if (_parkedFindings.Contains(findingKey)
            || (_failuresByFinding.TryGetValue(findingKey, out var failures) && failures >= _budget.AttemptsPerFinding))
        {
            throw new LoopBudgetExceededException(
                LoopBudgetExceededException.AttemptsBudgetCode,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Finding '{findingKey}' is parked after reaching the maximum attempt limit ({_budget.AttemptsPerFinding})."));
        }
    }
}
