final class WorkspaceSnapshot {
  WorkspaceSnapshot({
    required this.revision,
    required List<String> path,
    required List<String> open,
    List<WorkspaceViewReference> openViews = const [],
    this.focusedViewId,
    List<WorkspaceViewReference> pins = const [],
  }) : path = List.unmodifiable(path),
       open = List.unmodifiable(open),
       openViews = List.unmodifiable(openViews),
       pins = List.unmodifiable(pins);
  final int revision;
  final List<String> path, open;
  final List<WorkspaceViewReference> openViews, pins;
  final String? focusedViewId;

  factory WorkspaceSnapshot.fromJson(Map<String, dynamic> json) =>
      WorkspaceSnapshot(
        revision: json['revision'] as int,
        path: (json['path'] as List).cast<String>(),
        open: (json['open'] as List).cast<String>(),
        openViews: (json['openViews'] as List? ?? const [])
            .map(
              (value) => WorkspaceViewReference.fromJson(
                Map<String, dynamic>.from(value as Map),
              ),
            )
            .toList(),
        focusedViewId: json['focusedViewId'] as String?,
        pins: (json['pins'] as List? ?? const [])
            .map(
              (value) => WorkspaceViewReference.fromJson(
                Map<String, dynamic>.from(value as Map),
              ),
            )
            .toList(),
      );
}

final class WorkspaceViewReference {
  const WorkspaceViewReference({
    required this.id,
    required this.app,
    required this.variant,
    required this.context,
  });
  final String id, app, variant, context;
  factory WorkspaceViewReference.fromJson(Map<String, dynamic> json) =>
      WorkspaceViewReference(
        id: json['id'] as String,
        app: json['app'] as String,
        variant: json['variant'] as String,
        context: json['context'] as String,
      );

  Map<String, Object?> toJson() => {
    'id': id,
    'app': app,
    'variant': variant,
    'context': context,
  };

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is WorkspaceViewReference &&
          other.id == id &&
          other.app == app &&
          other.variant == variant &&
          other.context == context;

  @override
  int get hashCode => Object.hash(id, app, variant, context);
}
