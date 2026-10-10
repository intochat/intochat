namespace DigitalBrain.Testing.Module;

// The module tier's name for the one harness; deleted in Task 20 after the sweep.
public static class ModuleTest
{
    public static TestBrain Create() => TestBrain.Create();

    // The default brain is the Open posture's: account and brain "owner", as an HTTP caller reaches it.
    public const string DefaultAccount = Platform.Identity.AccountSession.DefaultLogin;
}
