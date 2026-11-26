namespace IntoChat.OS.Modules.LocalDisk;

public interface ILocalDisk
{
    Task<string[]> Roots();

    Task AllowRoot(string path);

    Task RevokeRoot(string path);
}
