using IntoChat.Cloud.Auth.Core;

namespace IntoChat.Cloud.Auth.Neurons;

internal static class AuthCodeKeys
{
    public static string ForSubject(string subjectHash, CodePurpose purpose)
        => IAuthCode.Key(CodeEngine.SubjectKeyOf(subjectHash), purpose);

    // The slot a code id belongs to; null for a malformed id.
    public static string? ForCodeId(string? codeId, out CodePurpose purpose)
        => CodeId.TryParse(codeId, out purpose, out var subject128, out _)
            ? IAuthCode.Key(Convert.ToHexStringLower(subject128), purpose)
            : null;
}
