using System.Net;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class DevAppAccessFacts
{
    private const string Secret = "secret-value";

    [Theory]
    [InlineData("localhost:5000", "application/json", "127.0.0.1", "Bearer secret-value", null)]
    [InlineData("127.0.0.1:5000", "application/json; charset=utf-8", "::1", "Bearer secret-value", null)]
    [InlineData("[::1]:5000", "application/json", "127.0.0.1", "Bearer secret-value", null)]
    [InlineData("evil.example", "application/json", "127.0.0.1", "Bearer secret-value", 400)]
    [InlineData("localhost:5000", "text/plain", "127.0.0.1", "Bearer secret-value", 415)]
    [InlineData("localhost:5000", "application/x-www-form-urlencoded", "127.0.0.1", "Bearer secret-value", 415)]
    [InlineData("localhost:5000", "application/json", "8.8.8.8", "Bearer secret-value", 403)]
    [InlineData("localhost:5000", "application/json", "10.1.0.5", "Bearer secret-value", 403)]
    [InlineData("localhost:5000", "application/json", "127.0.0.1", "Bearer wrong", 401)]
    [InlineData("localhost:5000", "application/json", "127.0.0.1", null, 401)]
    public void TheGateRejectsTheWrongCaller(string host, string contentType, string remote, string? authorization, int? status)
    {
        var address = IPAddress.Parse(remote);
        var decision = DevAppAccess.Reject(host, contentType, address, authorization, Secret, trustedGateway: "10.0.0.8");
        Assert.Equal(status, decision);
    }

    [Fact]
    public void ATrustedGatewayAddressIsAccepted()
    {
        var decision = DevAppAccess.Reject("localhost:5000", "application/json", IPAddress.Parse("10.0.0.8"), "Bearer secret-value", Secret, "http://10.0.0.8:8080");
        Assert.Null(decision);
    }

    [Theory]
    [InlineData("../x.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/x.cs")]
    [InlineData("nested/../../x.cs")]
    public void PathsOutsideTheFolderAreRejected(string path) => Assert.True(DevAppAccess.UnsafePath(path));

    [Fact]
    public void AnEmptyCatchIsDB0022()
    {
        var diagnostics = DevAppEndpoints.EmptyCatches(new Dictionary<string, string> { ["add.cs"] = "try { } catch { }\n" });
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("DB0022", diagnostic.Id);
        Assert.Equal("add.cs", diagnostic.File);
    }

    [Fact]
    public void RotateWritesANewOwnerToken()
    {
        var root = Directory.CreateTempSubdirectory("dev-token-").FullName;
        try
        {
            var path = Path.Combine(root, "dev-token");
            var first = DevAppAccess.Rotate(path);
            var second = DevAppAccess.Rotate(path);
            Assert.NotEqual(first, second);
            Assert.Equal(second, File.ReadAllText(path));
            Assert.DoesNotContain(second, "logged");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
