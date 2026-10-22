namespace IntoChat.OS.Sdk.Validation;

public interface IModuleValidator<in T>
{
    ValidationIssue[] Validate(T value);
}
