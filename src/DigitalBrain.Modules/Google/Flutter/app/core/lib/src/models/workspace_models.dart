final class WorkspaceWindow {
  const WorkspaceWindow({
    required this.id,
    required this.title,
    required this.kind,
    required this.neuronId,
    required this.isOpen,
  });
  final String id, title, kind, neuronId;
  final bool isOpen;
  factory WorkspaceWindow.fromJson(Map<String, dynamic> json) {
    final reference = Map<String, dynamic>.from(json['reference'] as Map);
    return WorkspaceWindow(
      id: json['id'] as String,
      title: json['title'] as String,
      kind: reference['kind'] as String,
      neuronId: reference['neuronId'] as String,
      isOpen: json['isOpen'] as bool,
    );
  }
}

final class WorkspaceSnapshot {
  WorkspaceSnapshot({
    required this.revision,
    required List<WorkspaceWindow> windows,
    this.firstRun,
    this.focusedWindow,
    this.home,
    this.companion,
  }) : windows = List.unmodifiable(windows);
  final int revision;
  final String? focusedWindow;
  final Map<String, dynamic>? home, companion;
  final List<WorkspaceWindow> windows;

  /// The assistant starter state of a workspace that has never opened a window; null afterwards.
  final Map<String, dynamic>? firstRun;
  factory WorkspaceSnapshot.fromJson(Map<String, dynamic> json) =>
      WorkspaceSnapshot(
        revision: json['revision'] as int,
        focusedWindow: json['focusedWindow'] as String?,
        home: json['home'] is Map
            ? Map<String, dynamic>.from(json['home'] as Map)
            : null,
        companion: json['companion'] is Map
            ? Map<String, dynamic>.from(json['companion'] as Map)
            : null,
        windows: (json['windows'] as List)
            .map(
              (w) =>
                  WorkspaceWindow.fromJson(Map<String, dynamic>.from(w as Map)),
            )
            .toList(),
        firstRun: json['firstRun'] is Map
            ? Map<String, dynamic>.from(json['firstRun'] as Map)
            : null,
      );
}

final class WorkspaceRevision {
  const WorkspaceRevision(this.workspaceId, this.revision);
  final String workspaceId;
  final int revision;
  factory WorkspaceRevision.fromJson(Map<String, dynamic> json) =>
      WorkspaceRevision(json['brainId'] as String, json['revision'] as int);
}
