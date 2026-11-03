namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

public sealed class TestHome : IDisposable
{
    public string Path { get; }

    public TestHome()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "intochat-os-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) { return; }
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(200);
            }
        }
    }
}
