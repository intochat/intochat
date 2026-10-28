using System.Text.RegularExpressions;
using OtpNet;

namespace IntoChat.Cloud.Auth.Core;

public readonly record struct TotpResult(bool Ok, long MatchedStep, bool Replay);

public sealed partial class TotpVerifier(TimeProvider timeProvider)
{
    private const int StepSeconds = 30;
    private const int Digits = 6;

    public TotpResult Verify(byte[] secret, string? code, long lastUsedStep)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (code is null || !SixDigits().IsMatch(code))
        {
            return new TotpResult(false, 0, false);
        }

        var totp = new Totp(secret, StepSeconds, OtpHashMode.Sha1, Digits);
        var matched = totp.VerifyTotp(timeProvider.GetUtcNow().UtcDateTime, code, out var step, new VerificationWindow(1, 1));
        if (!matched)
        {
            return new TotpResult(false, 0, false);
        }

        return step <= lastUsedStep
            ? new TotpResult(false, step, true)
            : new TotpResult(true, step, false);
    }

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex SixDigits();
}