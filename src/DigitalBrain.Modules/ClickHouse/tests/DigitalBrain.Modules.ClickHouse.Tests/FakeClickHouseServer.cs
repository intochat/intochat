using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Modules.ClickHouse.Tests;

// A ClickHouse HTTP endpoint behind the real driver: `SELECT version()` answers 24.1, every other
// statement one UInt8 column `value` holding 42, in RowBinaryWithNamesAndTypes.
internal sealed class FakeClickHouseServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _databases = new();

    private FakeClickHouseServer(WebApplication app) { _app = app; }

    // The database each request named, in arrival order.
    public IReadOnlyCollection<string> Databases => _databases;

    public string ConnectionString(string database)
    {
        var address = new Uri(_app.Urls.Single());
        return $"Host={address.Host};Port={address.Port};Protocol=http;Database={database};Username=reader";
    }

    public static async Task<FakeClickHouseServer> StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeClickHouseServer(app);
        app.Map("/", (Delegate)server.Respond);
        await app.StartAsync(ct);
        return server;
    }

    private async Task Respond(HttpContext http)
    {
        var sql = http.Request.Query["query"].ToString();
        if (http.Request.HasFormContentType) { sql += (await http.Request.ReadFormAsync(http.RequestAborted))["query"].ToString(); }
        else
        {
            // The driver gzips the statement it sends.
            var statement = http.Request.Headers.ContentEncoding == "gzip" ? new GZipStream(http.Request.Body, CompressionMode.Decompress) : http.Request.Body;
            using var reader = new StreamReader(statement);
            sql += await reader.ReadToEndAsync(http.RequestAborted);
        }
        _databases.Enqueue(http.Request.Query["database"].ToString());
        var body = sql.Contains("version()", StringComparison.Ordinal)
            ? Table("version()", "String", Text("24.1"))
            : Table("value", "UInt8", [42]);
        http.Response.ContentType = "application/octet-stream";
        await http.Response.Body.WriteAsync(body, http.RequestAborted);
    }

    private static byte[] Table(string column, string type, byte[] cell)
        => [.. Varint(1), .. Text(column), .. Text(type), .. cell];

    private static byte[] Text(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [.. Varint(bytes.Length), .. bytes];
    }

    private static byte[] Varint(int value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        }
        while (value != 0);
        return [.. bytes];
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
