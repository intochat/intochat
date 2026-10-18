using System.Net;
using System.Text.Json;
using DigitalBrain.Testing.Aspire.Diagnostics;

namespace DigitalBrain.Testing.Tests.Integration;

public sealed class DashboardTelemetryFacts
{
    [Fact]
    public void CompletionRequiresEveryRequestParentInTheOperationTrace()
    {
        var trace = System.Diagnostics.ActivityTraceId.CreateRandom();
        var foreignTrace = System.Diagnostics.ActivityTraceId.CreateRandom();
        var expected = new HashSet<string>(StringComparer.Ordinal) { "first", "second" };
        static DashboardSpan Completed(string parent, System.Diagnostics.ActivityTraceId id, DashboardSpanKind kind = DashboardSpanKind.Server)
            => new() { TraceId = id.ToHexString(), ParentSpanId = parent, Kind = kind, StartTimeUnixNano = 100, EndTimeUnixNano = 200 };

        Assert.False(TestTelemetry.RequestsCompleted(SpanData(Completed("first", trace)), trace, expected));
        Assert.False(TestTelemetry.RequestsCompleted(SpanData(Completed("first", trace), Completed("second", foreignTrace)), trace, expected));
        Assert.False(TestTelemetry.RequestsCompleted(SpanData(Completed("first", trace), Completed("second", trace, DashboardSpanKind.Client)), trace, expected));
        Assert.True(TestTelemetry.RequestsCompleted(SpanData(Completed("first", trace), Completed("second", trace)), trace, expected));
        Assert.False(TestTelemetry.RequestsCompleted(SpanData(), trace, new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void PrivacyIncludesEarlierSpansOverlappingTheWindowWithoutChargingThemToTheBudget()
    {
        var start = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var end = start.AddSeconds(1);
        var trace = System.Diagnostics.ActivityTraceId.CreateRandom();
        var earlier = new DashboardSpan { Name = "earlier", StartTimeUnixNano = 9_000_000_000, EndTimeUnixNano = 10_500_000_000 };
        var inside = new DashboardSpan { Name = "inside", TraceId = trace.ToHexString(), StartTimeUnixNano = 10_100_000_000, EndTimeUnixNano = 10_200_000_000 };
        var before = new DashboardSpan { Name = "before", StartTimeUnixNano = 8_000_000_000, EndTimeUnixNano = 9_000_000_000 };
        var after = new DashboardSpan { Name = "after", StartTimeUnixNano = 12_000_000_000, EndTimeUnixNano = 13_000_000_000 };

        var capture = TestTelemetry.Capture(SpanData(earlier, inside, before, after), new(), start, end, trace);

        Assert.Same(inside, Assert.Single(capture.AllSpans).Data);
        Assert.Same(inside, Assert.Single(capture.Spans).Data);
        Assert.Equal(["earlier", "inside"], capture.PrivacySpans.Select(span => span.Name).ToArray());
    }

    [Fact]
    public void FlattenPreservesDecodedSchemaAndUnknownWrapperMetadataForBothSignals()
    {
        const string resourceSchema = "<resource-schema>秘密";
        const string scopeSchema = "<scope-schema>\nprivate";
        const string rootSecret = "root-秘密";
        const string resourceSecret = "resource-\"private\"";
        const string scopeSecret = "scope-<private>&";
        static Dictionary<string, JsonElement> Fields(string secret) => new() { ["future"] = JsonSerializer.SerializeToElement(secret) };
        var data = new DashboardTelemetryData
        {
            AdditionalFields = Fields(rootSecret),
            ResourceSpans = [new DashboardResourceSpans
            {
                SchemaUrl = resourceSchema, AdditionalFields = Fields(resourceSecret),
                ScopeSpans = [new DashboardScopeSpans
                { SchemaUrl = scopeSchema, AdditionalFields = Fields(scopeSecret), Spans = [new DashboardSpan()] }],
            }],
            ResourceLogs = [new DashboardResourceLogs
            {
                SchemaUrl = resourceSchema, AdditionalFields = Fields(resourceSecret),
                ScopeLogs = [new DashboardScopeLogs
                { SchemaUrl = scopeSchema, AdditionalFields = Fields(scopeSecret), LogRecords = [new DashboardLogRecord()] }],
            }],
        };

        var spanText = Assert.Single(TestTelemetry.Flatten(data)).Text;
        var logText = Assert.Single(TestTelemetry.FlattenLogs(data)).Text;
        foreach (var secret in new[] { resourceSchema, scopeSchema, rootSecret, resourceSecret, scopeSecret })
        {
            Assert.Contains(secret, spanText, StringComparison.Ordinal);
            Assert.Contains(secret, logText, StringComparison.Ordinal);
        }
    }

    private static DashboardTelemetryData SpanData(params DashboardSpan[] spans)
        => new() { ResourceSpans = [new DashboardResourceSpans { ScopeSpans = [new DashboardScopeSpans { Spans = spans }] }] };

    [Fact]
    public void PrivacySearchDecodesStringsAndIncludesEventsAndUnknownFields()
    {
        const string secret = "<private>\n秘密\"&";
        var span = new TelemetrySpan(null, null, new DashboardSpan
        {
            Events = [new DashboardSpanEvent { Name = secret }],
            AdditionalFields = new() { ["future"] = JsonSerializer.SerializeToElement(secret) },
        });
        Assert.Contains(secret, span.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrelationDoesNotHideOtherWorkFromWholeWindowAssertions()
    {
        var trace = System.Diagnostics.ActivityTraceId.CreateRandom();
        var own = new TelemetrySpan(null, null, new DashboardSpan { TraceId = trace.ToHexString() });
        var other = new TelemetrySpan(null, null, new DashboardSpan { TraceId = System.Diagnostics.ActivityTraceId.CreateRandom().ToHexString() });
        var capture = new TelemetryCapture([own, other], [], trace);
        Assert.Same(own, Assert.Single(capture.Spans));
        Assert.Equal(2, capture.AllSpans.Count);
    }

    [Fact]
    public void DroppedEventAttributesInvalidateAbsenceAssertions()
    {
        var data = new DashboardTelemetryData
        {
            ResourceSpans = [new DashboardResourceSpans
        {
            ScopeSpans = [new DashboardScopeSpans { Spans = [new DashboardSpan
            { Events = [new DashboardSpanEvent { DroppedAttributesCount = 1 }] }] }],
        }]
        };
        Assert.Throws<InvalidDataException>(() => TestTelemetry.ValidateComplete(data, new()));
    }

    [Fact]
    public async Task QueryRetainsFullOtlpValuesAndRequestsAllMatchingRecords()
    {
        const string json = """
            {"totalCount":1,"returnedCount":1,"data":{"resourceSpans":[{
              "resource":{"attributes":[{"key":"service.name","value":{"stringValue":"brain"}}]},
              "schemaUrl":"resource-schema","scopeSpans":[{
                "scope":{"name":"DigitalBrain.AI","version":"1","attributes":[{"key":"scope","value":{"boolValue":true}}]},
                "schemaUrl":"scope-schema","spans":[{
                  "traceId":"0123456789abcdef0123456789abcdef","spanId":"0123456789abcdef",
                  "parentSpanId":"fedcba9876543210","traceState":"vendor=value","flags":257,"kind":3,
                  "name":"operation","startTimeUnixNano":"1706425800000000001","endTimeUnixNano":"1706425800000000101",
                  "attributes":[{"key":"typed","value":{"arrayValue":{"values":[
                    {"intValue":"9223372036854775807"},{"doubleValue":1.5},{"boolValue":false},{"bytesValue":"AQI="},
                    {"kvlistValue":{"values":[{"key":"nested","value":{"stringValue":"secret"}}]}}
                  ]}}}],
                  "events":[{"name":"event","timeUnixNano":"1706425800000000051","attributes":[{"key":"event","value":{"stringValue":"event-secret"}}]}],
                  "links":[{"traceId":"other-trace","spanId":"other-span","traceState":"linked","flags":1,"attributes":[{"key":"link","value":{"stringValue":"link-secret"}}]}],
                  "status":{"code":2,"message":"status-secret"},"futureField":{"private":"future-secret"}
                }]}]}]}}
            """;
        using var handler = new ResponseHandler(json);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18888/") };
        var data = await new DashboardTelemetryClient(http).GetSpansAsync("trace/value", TestContext.Current.CancellationToken);
        Assert.Equal("?limit=2147483647&traceId=trace%2Fvalue", handler.RequestUri!.Query);
        var resource = Assert.Single(data.ResourceSpans);
        Assert.Equal("brain", Assert.Single(resource.Resource!.Attributes).Value!.StringValue);
        var scope = Assert.Single(resource.ScopeSpans);
        Assert.Equal("1", scope.Scope!.Version);
        var span = Assert.Single(scope.Spans);
        Assert.Equal(1706425800000000001UL, span.StartTimeUnixNano);
        var values = Assert.Single(span.Attributes).Value!.ArrayValue!.Values;
        Assert.Equal(long.MaxValue, values[0].IntValue);
        Assert.Equal(1.5, values[1].DoubleValue);
        Assert.False(values[2].BoolValue);
        Assert.Equal("AQI=", values[3].BytesValue);
        Assert.Equal("secret", Assert.Single(values[4].KvlistValue!.Values).Value!.StringValue);
        Assert.Equal("event-secret", Assert.Single(Assert.Single(span.Events).Attributes).Value!.StringValue);
        Assert.Equal("link-secret", Assert.Single(Assert.Single(span.Links).Attributes).Value!.StringValue);
        Assert.Equal("status-secret", span.Status!.Message);
        Assert.Equal("future-secret", span.AdditionalFields!["futureField"].GetProperty("private").GetString());
        Assert.Contains("future-secret", JsonSerializer.Serialize(data, DashboardTelemetryClient.JsonOptions));
    }

    [Fact]
    public async Task LogsRetainStructuredBodiesCorrelationAndObservedTimestamp()
    {
        const string json = """
            {"totalCount":1,"returnedCount":1,"data":{"resourceLogs":[{"scopeLogs":[{"logRecords":[{
              "timeUnixNano":"1","observedTimeUnixNano":"2","severityNumber":17,"severityText":"Error",
              "traceId":"trace","spanId":"span","flags":1,"eventName":"error-event",
              "body":{"kvlistValue":{"values":[{"key":"message","value":{"stringValue":"private"}}]}},
              "futureLogField":"preserved"
            }]}]}]}}
            """;
        using var handler = new ResponseHandler(json);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18888/") };
        var data = await new DashboardTelemetryClient(http).GetLogsAsync(null, TestContext.Current.CancellationToken);
        var log = Assert.Single(Assert.Single(Assert.Single(data.ResourceLogs).ScopeLogs).LogRecords);
        Assert.Equal(2UL, log.ObservedTimeUnixNano);
        Assert.Equal("trace", log.TraceId);
        Assert.Equal("private", Assert.Single(log.Body!.KvlistValue!.Values).Value!.StringValue);
        Assert.Equal("preserved", log.AdditionalFields!["futureLogField"].GetString());
    }

    [Theory]
    [InlineData("{\"data\":{},\"totalCount\":201,\"returnedCount\":200}")]
    [InlineData("{\"data\":{},\"totalCount\":1,\"returnedCount\":1}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":{\"resourceSpans\":null},\"totalCount\":0,\"returnedCount\":0}")]
    public async Task IncompleteSnapshotsCannotBecomePassingAssertions(string json)
    {
        using var handler = new ResponseHandler(json);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18888/") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DashboardTelemetryClient(http)
            .GetSpansAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AuthenticationErrorsAreNotTreatedAsEmptyTelemetry()
    {
        using var handler = new ResponseHandler("{}", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:18888/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => new DashboardTelemetryClient(http)
            .GetLogsAsync(null, TestContext.Current.CancellationToken));
    }

    private sealed class ResponseHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
