using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using IntoChat.OS.Modules.Apps.Contracts.Testing;

namespace IntoChat.OS.Modules.Apps.Testing;

internal static class TrxReportParser
{
    public static TestExecutionResult Parse(string trxFilePath, long elapsedMilliseconds)
    {
        if (!File.Exists(trxFilePath))
        {
            return new TestExecutionResult(
                Success: false,
                Total: 0,
                Passed: 0,
                Failed: 1,
                Skipped: 0,
                DurationMs: elapsedMilliseconds,
                Summary: "TRX report file was not found.",
                Failures: [new TestFailureDetail("TRX", "TRX report file was not generated or found at " + trxFilePath)]);
        }

        var text = File.ReadAllText(trxFilePath);
        return ParseXml(text, elapsedMilliseconds);
    }

    public static TestExecutionResult ParseXml(string xmlContent, long elapsedMilliseconds)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var counters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");

            int total = 0, passed = 0, failed = 0, skipped = 0;
            if (counters is not null)
            {
                int.TryParse(counters.Attribute("total")?.Value, out total);
                int.TryParse(counters.Attribute("passed")?.Value, out passed);
                int.TryParse(counters.Attribute("failed")?.Value, out var f);
                int.TryParse(counters.Attribute("error")?.Value, out var err);
                int.TryParse(counters.Attribute("timeout")?.Value, out var to);
                failed = f + err + to;
                int.TryParse(counters.Attribute("notExecuted")?.Value, out var ne);
                int.TryParse(counters.Attribute("skipped")?.Value, out var sk);
                skipped = ne + sk;
            }

            var failures = new List<TestFailureDetail>();
            foreach (var unitTestResult in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
            {
                var outcome = unitTestResult.Attribute("outcome")?.Value;
                if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(outcome, "Error", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase))
                {
                    var testName = unitTestResult.Attribute("testName")?.Value ?? "UnknownTest";
                    var errorInfo = unitTestResult.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
                    var message = errorInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value ?? "Test failed.";
                    var stackTrace = errorInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "StackTrace")?.Value;
                    failures.Add(new TestFailureDetail(testName, message, stackTrace));
                }
            }

            if (failures.Count > failed)
            {
                failed = failures.Count;
            }

            if (counters is null)
            {
                var allResults = doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult").ToArray();
                total = allResults.Length;
                passed = allResults.Count(e => string.Equals(e.Attribute("outcome")?.Value, "Passed", StringComparison.OrdinalIgnoreCase));
                failed = failures.Count;
                skipped = Math.Max(0, total - passed - failed);
            }

            bool success = failed == 0 && failures.Count == 0 && (passed > 0 || total == 0);
            string summary = $"Total: {total}, Passed: {passed}, Failed: {failed}, Skipped: {skipped}";

            return new TestExecutionResult(
                Success: success,
                Total: total,
                Passed: passed,
                Failed: failed,
                Skipped: skipped,
                DurationMs: elapsedMilliseconds,
                Summary: summary,
                Failures: failures);
        }
        catch (Exception ex)
        {
            return new TestExecutionResult(
                Success: false,
                Total: 0,
                Passed: 0,
                Failed: 1,
                Skipped: 0,
                DurationMs: elapsedMilliseconds,
                Summary: "Failed to parse TRX XML: " + ex.Message,
                Failures: [new TestFailureDetail("TrxParser", ex.Message, ex.StackTrace)]);
        }
    }
}
