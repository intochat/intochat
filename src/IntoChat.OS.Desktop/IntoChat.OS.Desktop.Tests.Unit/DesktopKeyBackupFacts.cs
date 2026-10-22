using System.Security.Cryptography;
using System.Text.Json;

namespace IntoChat.OS.Desktop.Tests.Unit;

// Uninstalling the MSIX deletes LocalState, and with it the DPAPI-protected master key; the passphrase backup is the way back.
public sealed class DesktopKeyBackupFacts
{
    // A fresh 32-byte key per run, shaped like the launcher's own.
    private static readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private const int FastIterations = 1_000;

    [Fact]
    public void ExportThenImportWithThePassphraseReturnsTheKey()
    {
        var backup = DesktopKeyBackup.Export(Key, "correct horse battery staple", FastIterations);
        Assert.Equal(Key, DesktopKeyBackup.Import(backup, "correct horse battery staple"));
    }

    [Fact]
    public void TheBackupNeverContainsTheKey()
    {
        var backup = DesktopKeyBackup.Export(Key, "correct horse battery staple", FastIterations);
        Assert.DoesNotContain(Key, backup, StringComparison.Ordinal);
        Assert.DoesNotContain(Key.TrimEnd('='), backup, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoExportsOfTheSameKeyDiffer()
        => Assert.NotEqual(DesktopKeyBackup.Export(Key, "correct horse battery staple", FastIterations),
            DesktopKeyBackup.Export(Key, "correct horse battery staple", FastIterations));

    [Fact]
    public void AWrongPassphraseIsRefused()
    {
        var backup = DesktopKeyBackup.Export(Key, "correct horse battery staple", FastIterations);
        var failure = Assert.Throws<CryptographicException>(() => DesktopKeyBackup.Import(backup, "wrong horse battery staple"));
        Assert.Contains("passphrase", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("salt")]
    [InlineData("nonce")]
    [InlineData("tag")]
    public void ATamperedBackupIsRefused(string field)
    {
        var backup = DesktopKeyBackup.Export(Key, "correct horse battery staple", FastIterations);
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(backup)!;
        var bytes = Convert.FromBase64String(document[field].GetString()!);
        bytes[0] ^= 0x01;
        var tampered = document.ToDictionary(pair => pair.Key, pair => pair.Key == field ? (object)Convert.ToBase64String(bytes) : pair.Value);
        Assert.Throws<CryptographicException>(() => DesktopKeyBackup.Import(JsonSerializer.Serialize(tampered), "correct horse battery staple"));
    }

    [Fact]
    public void ALoweredIterationCountIsRefusedBecauseItIsAuthenticated()
    {
        var backup = DesktopKeyBackup.Export(Key, "correct horse battery staple", FastIterations);
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(backup)!;
        var tampered = document.ToDictionary(pair => pair.Key, pair => pair.Key == "iterations" ? (object)(FastIterations + 1) : pair.Value);
        Assert.Throws<CryptographicException>(() => DesktopKeyBackup.Import(JsonSerializer.Serialize(tampered), "correct horse battery staple"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("eleven char")]
    public void AWeakPassphraseIsRefusedOnExport(string passphrase)
        => Assert.Throws<ArgumentException>(() => DesktopKeyBackup.Export(Key, passphrase, FastIterations));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"something-else\",\"version\":1}")]
    [InlineData("{\"format\":\"intochat-os-master-key\",\"version\":2}")]
    public void AnUnreadableBackupIsRefused(string backup)
        => Assert.Throws<CryptographicException>(() => DesktopKeyBackup.Import(backup, "correct horse battery staple"));

    [Fact]
    public void TheDefaultWorkFactorMeetsCurrentGuidance()
    {
        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(DesktopKeyBackup.Export(Key, "correct horse battery staple"))!;
        Assert.Equal("pbkdf2-sha256", document["kdf"].GetString());
        Assert.True(document["iterations"].GetInt32() >= 600_000);
    }

    [Fact]
    public void ImportWritesTheKeyOnlyWhereNoneExists()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var target = Path.Combine(directory.FullName, "master-key.dpapi");
            File.WriteAllText(target, "existing");
            var failure = Assert.Throws<InvalidOperationException>(() => DesktopKeyBackup.RequireNoKey(target));
            Assert.Contains(target, failure.Message, StringComparison.Ordinal);
            File.Delete(target);
            DesktopKeyBackup.RequireNoKey(target);
        }
        finally { directory.Delete(true); }
    }
}
