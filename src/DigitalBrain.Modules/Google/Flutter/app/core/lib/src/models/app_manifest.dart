// Mirrors DigitalBrain.Apps.AppManifest and AppToolbarEntry as the shell reads them from
// GET /brains/{brain}/apps/{name} under "manifest".

enum AppToolbarKind {
  button,
  toggle,
  slider,
  text;

  // The server validates kinds; an unknown one is wire drift and shows as a button.
  static AppToolbarKind parse(String? value) =>
      values.firstWhere((kind) => kind.name == value, orElse: () => button);
}

class AppToolbarEntry {
  const AppToolbarEntry({
    required this.operation,
    this.kind = AppToolbarKind.button,
    this.icon,
    this.label,
    this.min,
    this.max,
  });
  final String operation;
  final AppToolbarKind kind;
  final String? icon, label;
  final double? min, max;
  factory AppToolbarEntry.fromJson(Map<String, dynamic> json) =>
      AppToolbarEntry(
        operation: json['operation'] as String,
        kind: AppToolbarKind.parse(json['kind'] as String?),
        icon: json['icon'] as String?,
        label: json['label'] as String?,
        min: (json['min'] as num?)?.toDouble(),
        max: (json['max'] as num?)?.toDouble(),
      );
}

class AppManifest {
  const AppManifest({
    required this.title,
    required this.description,
    required this.operations,
    this.icon,
    this.toolbar = const [],
  });
  final String title, description;
  final List<String> operations;
  final String? icon;
  final List<AppToolbarEntry> toolbar;
  factory AppManifest.fromJson(Map<String, dynamic> json) => AppManifest(
    title: json['title'] as String? ?? '',
    description: json['description'] as String? ?? '',
    operations: [
      for (final operation in json['operations'] as List? ?? const [])
        Map<String, dynamic>.from(operation as Map)['name'] as String,
    ],
    icon: json['icon'] as String?,
    toolbar: [
      for (final entry in json['toolbar'] as List? ?? const [])
        AppToolbarEntry.fromJson(Map<String, dynamic>.from(entry as Map)),
    ],
  );
}
