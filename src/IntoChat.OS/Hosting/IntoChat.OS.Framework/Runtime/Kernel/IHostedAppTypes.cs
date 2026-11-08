// os-only: the OS edge (Flutter) asks the Apps module whether the host loaded an app's compiled code.
namespace IntoChat.OS.Framework;

// Kernel provenance for an app: true only when the host itself loaded the app's code, i.e. the type assigned to the app key
// comes from an assembly the composition registered as compiled apps. A setting naming a type proves nothing; this does.
public interface IHostedAppTypes
{
    bool IsHostLoaded(string appKey);
}
