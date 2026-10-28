using IntoChat.Cloud.Mail.Templates;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.Cloud.Auth.Http;

// POST /auth/signup (AUTH-01). New, unverified and verified addresses get the same 202 body and cost the same (a PBKDF2
// hash on every branch, mail off the request path); a verified address gets an AccountAlreadyExists notice instead of a
// code (W4 D10).
internal static class SignUpEndpoints
{
    public const string Path = "/auth/signup";
    internal static readonly string[] EmailMethod = ["email"];

    public static void Map(IEndpointRouteBuilder endpoints) => endpoints.MapPost(Path, SignUpAsync).AllowAnonymous().AddEndpointFilter<IpThrottleFilter>();

    private static async Task<IResult> SignUpAsync(HttpRequest request, AuthCalls calls, AuthHashing hashing, AuthModuleOptions options, TimeProvider clock)
    {
        var (body, failure) = await AuthProblems.ReadAsync<SignUpRequest>(request);
        if (failure is not null)
        {
            return failure;
        }

        if (body!.Consent != true)
        {
            return AuthProblems.BadRequest(AuthErrors.UserConsentNotGranted);
        }

        if (EmailNormalizer.Normalize(body.Email) is not { } email)
        {
            return AuthProblems.BadRequest(AuthProblems.EmailInvalid);
        }

        if (!PasswordHasher.IsValidLength(body.Password))
        {
            return AuthProblems.BadRequest(AuthProblems.PasswordInvalid);
        }

        var language = Language(body.Language);
        var displayName = DisplayName(body.DisplayName, email);
        var emailHash = hashing.EmailSubject(email);
        var passwordHash = new PasswordHasher(options.PasswordIterations).Hash(body.Password!);
        var entry = await calls.EmailIndex(emailHash, index => index.Get());
        var verified = entry.Status is EmailIndexStatus.Verified or EmailIndexStatus.ChangePending;
        if (!verified)
        {
            await calls.EmailIndex(emailHash, index => index.SetPending(new PendingSignUp(email, passwordHash, displayName, language, clock.GetUtcNow())));
        }

        var delivery = new CodeDelivery(email, language, displayName, MailTemplateId.SignupCode, verified ? MailTemplateId.AccountAlreadyExists : null);
        var issue = await calls.Code(emailHash, CodePurpose.Signup, code => code.Issue(
            new CodeIssueRequest(emailHash, IsDecoy: verified, EnforceCooldown: true, delivery, EmailMethod, "pwd")));
        return AuthProblems.Issued(issue);
    }

    internal static string Language(string? language) => language?.Trim().ToLowerInvariant() is "ru" ? "ru" : "en";

    internal static string DisplayName(string? displayName, string email)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsControl))
        {
            return email[..email.IndexOf('@')];
        }

        return trimmed.Length > 100 ? trimmed[..100] : trimmed;
    }

    internal sealed record SignUpRequest(string? Email, string? Password, string? DisplayName, string? Language, bool? Consent);
}

