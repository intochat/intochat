import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';

import 'workspace_store.dart';

// The working area: exactly one window, the focused one, filling it. Every other open window is
// "running" and reachable from Home. Chrome is the title bar and close; the editor keeps its
// identity while its content changes.
class WorkspaceDesktop extends StatefulWidget {
  const WorkspaceDesktop({
    super.key,
    required this.store,
    required this.editorBuilder,
    this.editorStateToken,
  });
  final WorkspaceStore store;
  final Widget Function(WorkspaceArtifact) editorBuilder;
  final Object? Function(WorkspaceArtifact)? editorStateToken;
  @override
  State<WorkspaceDesktop> createState() => _WorkspaceDesktopState();
}

class _WorkspaceDesktopState extends State<WorkspaceDesktop> {
  WorkspaceStore get store => widget.store;

  Object _editorToken(WorkspaceArtifact artifact) => Object.hash(
    artifact.title,
    artifact.kind,
    artifact.content,
    artifact.data['_dirty'],
    artifact.data['_revision'],
    widget.editorStateToken?.call(artifact),
  );

  @override
  Widget build(BuildContext context) {
    final id = store.focusedWindow;
    final artifact = id == null
        ? null
        : store.currentProject.artifacts.where((a) => a.id == id).firstOrNull;
    if (artifact == null) {
      return const SizedBox.shrink(key: ValueKey('desktop-empty'));
    }
    final colors = Theme.of(context).colorScheme;
    return Material(
      key: ValueKey('window-${artifact.id}'),
      color: colors.surfaceContainerLow,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(10),
        side: BorderSide(color: colors.outlineVariant),
      ),
      clipBehavior: Clip.antiAlias,
      child: Column(
        children: [
          SizedBox(
            height: 44,
            child: Row(
              children: [
                const SizedBox(width: 12),
                Icon(
                  uiArtifactIcon(artifact.kind),
                  size: 17,
                  color: colors.onSurfaceVariant,
                ),
                const SizedBox(width: 9),
                Expanded(
                  child: Text(
                    artifact.title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ),
                IconButton(
                  key: ValueKey('window-close-${artifact.id}'),
                  tooltip: 'Close window',
                  onPressed: () => store.closeArtifact(artifact.id),
                  icon: const Icon(Icons.close, size: 16),
                  style: IconButton.styleFrom(
                    minimumSize: const Size(32, 40),
                    padding: const EdgeInsets.all(7),
                    tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                  ),
                ),
                const SizedBox(width: 3),
              ],
            ),
          ),
          Divider(height: 1, color: colors.outlineVariant.withValues(alpha: .6)),
          Expanded(
            child: RepaintBoundary(
              child: _PinnedEditor(
                token: _editorToken(artifact),
                builder: () => widget.editorBuilder(artifact),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _PinnedEditor extends StatefulWidget {
  const _PinnedEditor({required this.token, required this.builder});
  final Object token;
  final Widget Function() builder;
  @override
  State<_PinnedEditor> createState() => _PinnedEditorState();
}

class _PinnedEditorState extends State<_PinnedEditor> {
  late Widget _child = widget.builder();
  @override
  void reassemble() {
    super.reassemble();
    _child = widget.builder();
  }

  @override
  void didUpdateWidget(_PinnedEditor old) {
    super.didUpdateWidget(old);
    if (old.token != widget.token) _child = widget.builder();
  }

  @override
  Widget build(BuildContext context) => _child;
}