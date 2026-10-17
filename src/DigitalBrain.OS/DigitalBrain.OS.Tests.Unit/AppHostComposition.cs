namespace DigitalBrain.OS.Tests.Unit;

// Aspire's testing builder finds the AppHost's builder through a process-wide diagnostic listener, so two
// builders created at once take each other's Build() call and both wait forever. Every test that builds
// the AppHost model joins this collection, which xunit runs one class at a time.
public static class AppHostComposition
{
    public const string Collection = "AppHost composition";
}
