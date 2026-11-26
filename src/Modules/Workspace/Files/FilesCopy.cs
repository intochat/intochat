namespace IntoChat.OS.Modules.Apps;

// Copy is the 07 UI spec. Callers do not invent their own sentences.
internal static class FilesCopy
{
    public const string NoStorageHeading = "No storage connected";
    public const string NoStorageBody = "Connect Google Drive or choose folders on this computer to browse files.";
    public const string OpenSettings = "Open Settings";
    public const string StorageHeading = "Storage";
    public const string Unavailable = "Unavailable";
    public const string SpaceUnknown = "Space unknown";
    public const string TryAgain = "Try again";
    public const string UnavailableBody = "Files could not reach it. Check the connection in Settings, then try again.";
    public const string Loading = "Loading files";
    public const string EmptyHeading = "This folder is empty";
    public const string EmptyBody = "Upload a file or create a folder to get started.";
    public const string FilterLabel = "Filter by name";
    public const string NoMatchHeading = "No files match";
    public const string ClearFilter = "Clear filter";
    public const string SearchLabel = "Search all storages";
    public const string SearchButton = "Search";
    public const string SearchMissed = "Some storages did not respond";
    public const string Up = "Up";
    public const string LoadMore = "Load more files";
    public const string BackToFolder = "Back to folder";
    public const string BackToFiles = "Back to files";
    public const string OpenApp = "Open app";
    public const string PreviewLimited = "Preview shows the first 256 KB.";
    public const string PreviewUnavailable = "No preview for this file type. Use Download to open it.";
    public const string PreviewTooLarge = "This file is too large to preview. Use Download to open it.";
    public const string DeniedTitle = "That location is not available";
    public const string DeniedBody = "Files only works inside the folders you allowed in Settings.";
    public const string ListFailed = "Could not list this folder";
    public const string NothingChanged = "Nothing was changed.";
    public const string NameHeader = "Name";
    public const string SizeHeader = "Size";
    public const string ModifiedHeader = "Modified";
    public const string Upload = "Upload";
    public const string NewFolder = "New folder";
    public const string Rename = "Rename";
    public const string Move = "Move";
    public const string Copy = "Copy";
    public const string CopyNeuronId = "Copy Neuron ID";
    public const string CopyNeuronUri = "Copy neuron:// URI";
    public const string CopyNeuronUriShortcut = "Ctrl+Shift+C";
    public const string CopyDisplayPath = "Copy Display Path";
    public const string AskInoAboutFile = "Ask Ino about this file";
    public const string QuickViewHeading = "QuickView";
    public const string Download = "Download";
    public const string Delete = "Delete";

    public static string UnavailableTitle(string storage) => storage + " is unavailable";

    public static string NoMatchBody(string filter) => "Nothing in this folder has \"" + filter + "\" in its name. Clear the filter to see all files.";

    public static string SearchMissedBody(string storages) => "Results below are missing " + storages + ". Search again to retry.";

    public static string GroupSilent(string storage) => storage + " did not respond. Try again.";

    public static string NoMatchesIn(string storage) => "No matches in " + storage + ".";

    public static string Bounded(int count) => "Showing the first " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " matches. Narrow the search to see more.";

    public static string NothingNamed(string query) => "No files named \"" + query + "\" were found in the storages that responded.";
}
