using DigitalBrain;
namespace DigitalBrain.Microsoft.Playwright;

[Alias("playwright")]
[Orleans.Metadata.DefaultGrainType("playwright")]
public interface IPlaywright : INeuron
{
    Task Start(CancellationToken ct = default);
    Task Stop(CancellationToken ct = default);

    // Connector handoff. Apps consume Read, BrowserReady and BrowserUnavailable instead.
    Task Attach(BrowserAttachment attachment, CancellationToken ct = default);
    Task Detach(string sessionId, CancellationToken ct = default);
    Task<BrowserObservation> Navigate(string url, CancellationToken ct = default);
    Task<BrowserObservation> Snapshot(CancellationToken ct = default);
    Task<BrowserObservation> Click(string selector, CancellationToken ct = default);
    Task<BrowserObservation> Fill(string selector, string value, CancellationToken ct = default);
    // Runs a JavaScript expression in the attached page and returns its awaited value as JSON text.
    // Only pages on a deployment app origin (PlaywrightModuleOptions.AppOrigins) accept it.
    Task<string> Evaluate(string expression, CancellationToken ct = default);
    Task<BrowserSession> Read();
}
