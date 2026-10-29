using IntoChat.Cloud.Auth.Core;
using Microsoft.Extensions.Time.Testing;
using OtpNet;

namespace IntoChat.Cloud.Auth.Core.Tests;

public sealed class TotpVerifierTests
{
    private static readonly byte[] Secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");

    private static TotpVerifier At(long unixSeconds) =>
        new(new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));

    private static string CodeAt(long unixSeconds) =>
        new Totp(Secret).ComputeTotp(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    [InlineData(20000000000, "353130")]
    public void Verify_Rfc6238Vectors_Accepted(long time, string code)
    {
        var result = At(time).Verify(Secret, code, 0);

        Assert.True(result.Ok);
        Assert.Equal(time / 30, result.MatchedStep);
    }

    [Fact]
    public void Verify_StepMinusAndPlusOne_Accepted()
    {
        var now = 1_700_000_000L;

        Assert.True(At(now).Verify(Secret, CodeAt(now - 30), 0).Ok);
        Assert.True(At(now).Verify(Secret, CodeAt(now + 30), 0).Ok);
    }

    [Fact]
    public void Verify_StepPlusMinusTwo_RejectedOutsideWindow()
    {
        var now = 1_700_000_000L;

        Assert.False(At(now).Verify(Secret, CodeAt(now - 60), 0).Ok);
        Assert.False(At(now).Verify(Secret, CodeAt(now + 60), 0).Ok);
    }

    [Fact]
    public void Verify_SameStepTwice_ReplayRejected()
    {
        var now = 1_700_000_000L;
        var first = At(now).Verify(Secret, CodeAt(now), 0);
        var again = At(now).Verify(Secret, CodeAt(now), first.MatchedStep);

        Assert.True(first.Ok);
        Assert.False(again.Ok);
        Assert.True(again.Replay);
    }

    [Fact]
    public void Verify_OlderStepThanLastUsed_ReplayRejected()
    {
        var now = 1_700_000_000L;
        var result = At(now).Verify(Secret, CodeAt(now - 30), now / 30);

        Assert.False(result.Ok);
        Assert.True(result.Replay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData(" 12345")]
    public void Verify_MalformedCode_Rejected(string? code)
    {
        Assert.False(At(59).Verify(Secret, code, 0).Ok);
    }

    [Fact]
    public void Enrolment_ProducesSecretAndUri()
    {
        var enrolment = TotpEnrolment.Create("a b@x.io");

        Assert.Equal(20, enrolment.Secret.Length);
        Assert.Equal(Base32Encoding.ToString(enrolment.Secret), enrolment.Base32Secret);
        Assert.Equal(
            $"otpauth://totp/IntoChat:a%20b%40x.io?secret={enrolment.Base32Secret}&issuer=IntoChat&digits=6&period=30",
            enrolment.OtpAuthUri);
    }
}
