using System.Threading.Tasks;
using IntoChat.OS.Framework.Modules.Http;
using IntoChat.OS.Framework.Modules.Validation;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Framework.Generators.SampleModule;

public static class SampleHttp
{
    public static async Task<IResult> PreviewAsync(HttpContext context)
    {
        var (body, failure) = await HttpProblems.ReadValidatedAsync(context.Request, new SamplePreviewValidator(),
            maxBodyBytes: 1024, cancellationToken: context.RequestAborted);
        return failure ?? Results.Ok(new { accepted = body is not null });
    }
}

public sealed record SamplePreviewRequest(string? Text);

public sealed class SamplePreviewValidator : IModuleValidator<SamplePreviewRequest>
{
    public ValidationIssue[] Validate(SamplePreviewRequest value)
    {
        var rules = new ValidationRules();
        rules.Required("text", value.Text);
        rules.StringLength("text", value.Text, 1, 100);
        rules.Utf8Length("text", value.Text, 256);
        return rules.Issues;
    }
}
