final class WorkspaceSnapshot {
  WorkspaceSnapshot({
    required this.revision,
    required List<String> path,
    required List<String> open,
  }) : path = List.unmodifiable(path),
       open = List.unmodifiable(open);
  final int revision;
  final List<String> path, open;

  factory WorkspaceSnapshot.fromJson(Map<String, dynamic> json) =>
      WorkspaceSnapshot(
        revision: json['revision'] as int,
        path: (json['path'] as List).cast<String>(),
        open: (json['open'] as List).cast<String>(),
      );
}
