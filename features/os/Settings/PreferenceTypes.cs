using System;

// The stored preferences with their versions, and what a Save request carries.
public sealed record PreferenceState(string DisplayName, string Theme, int NameVersion, int ThemeVersion);
public sealed record Submission(string DisplayName, string Theme, PreferenceState? Baseline);

public sealed class PreferenceConflictException(string message) : Exception(message);
