using IntoChat.OS.Flutter.Table;

namespace IntoChat.OS.Flutter;

internal sealed record TableReplace(string Title, IReadOnlyList<TableColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);
