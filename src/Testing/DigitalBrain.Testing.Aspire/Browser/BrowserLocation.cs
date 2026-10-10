namespace DigitalBrain.Testing.Aspire;

// Where Chromium runs. A container needs only Docker; a local browser needs `playwright install`
// and is what a person debugging wants, because only it can show a window.
public enum BrowserLocation { Container, Local }
