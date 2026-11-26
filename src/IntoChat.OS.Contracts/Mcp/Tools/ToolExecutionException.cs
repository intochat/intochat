namespace IntoChat.OS.Modules.Tools;

public class ToolExecutionException : Exception
{
    public string ToolName { get; }
    public bool IsNotFound { get; }
    public bool IsValidationError { get; }

    public ToolExecutionException(string toolName, string message, bool isNotFound = false, bool isValidationError = false)
        : base($"Tool '{toolName}' execution failed: {message}")
    {
        ToolName = toolName;
        IsNotFound = isNotFound;
        IsValidationError = isValidationError;
    }

    public ToolExecutionException(string toolName, string message, Exception innerException, bool isNotFound = false, bool isValidationError = false)
        : base($"Tool '{toolName}' execution failed: {message}", innerException)
    {
        ToolName = toolName;
        IsNotFound = isNotFound;
        IsValidationError = isValidationError;
    }
}
