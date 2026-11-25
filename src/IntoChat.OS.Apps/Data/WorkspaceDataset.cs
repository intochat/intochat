using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Data;

// The dataset a query names, read from the brain's workspace through a screen's IWorkspaceFiles slot: Data Studio and Ino
// run the query on its text (DataAnalyticsEngine.ExecuteInMemory).
internal static class WorkspaceDataset
{
    // The workspace file the query names ("sales.csv", "reports/q4.csv" or "/work/q4.csv"; a name without an extension is
    // tried as a .csv file), when there is one: its stored path and its text.
    public static async Task<(string Path, string Text)?> Read(IWorkspaceFiles? files, string query)
    {
        if (files is null || DataAnalyticsEngine.TryExtractDatasetFileToken(query) is not { } token) { return null; }
        string[] candidates = Path.HasExtension(token) ? [token] : [token, token + ".csv"];
        foreach (var candidate in candidates)
        {
            try
            {
                if (await files.Find(candidate) is { IsFolder: false } file && await files.Read(candidate) is { Length: > 0 } bytes)
                {
                    return (file.Path, System.Text.Encoding.UTF8.GetString(bytes));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For(typeof(WorkspaceDataset)).LogSwallowed(error, "reading a dataset from the workspace");
            }
        }
        return null;
    }
}
