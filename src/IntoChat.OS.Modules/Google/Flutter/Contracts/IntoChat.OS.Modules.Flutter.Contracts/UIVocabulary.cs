namespace IntoChat.OS.Modules.Flutter;

public static class UIVocabulary
{
    // The kept UI kinds: one View tree, its fields, and the hosted surfaces.
    public const string ViewType = "view";
    public const string FieldType = "ui.field";
    public const string ConfirmationType = "confirmation";
    public const string ViewportType = "viewport";
    public const string WebBrowserType = "webbrowser";

    // Widget neurons non-UI modules still read or write (Kernel, Apps, Circles, features/os); each goes with its last user.
    public const string TextType = "text";
    public const string ButtonType = "button";
    public const string LayoutType = "layout";
    public const string CollectionType = "collection";
    public const string InfoBarType = "infobar";
    public const string CardType = "card";
    public const string ImageType = "image";
    public const string ChartType = "chart";
    public const string TableType = "table";
    public const string PersonType = "person";
    public const string FormType = "form";

    public const int ChartMaxPoints = 256;
}
