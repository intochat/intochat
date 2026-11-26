import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/controls.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_tokens.dart';
import '../theme/wire_node_values.dart';
import 'node_context.dart';

Widget renderDiffCard(DiffCardNode node, NodeContext ctx) =>
    DiffCardView(node: node, ctx: ctx);

/// Unified diff hunk viewer card for Feature-to-PR & self-improving code review.
///
/// Displays repository, target branch, short SHA badge, tabular `+add / -del`
/// counts, collapsible per-file sections, and unified diff hunks with `+`/`-`
/// tinting and line gutters.
class DiffCardView extends StatefulWidget {
  const DiffCardView({super.key, required this.node, required this.ctx});

  final DiffCardNode node;
  final NodeContext ctx;

  @override
  State<DiffCardView> createState() => _DiffCardViewState();
}

class _DiffCardViewState extends State<DiffCardView> {
  final Set<String> _collapsedPaths = <String>{};
  bool _initialized = false;

  @override
  void initState() {
    super.initState();
    _syncDefaultCollapsed();
  }

  @override
  void didUpdateWidget(covariant DiffCardView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.node.id != widget.node.id) {
      _collapsedPaths.clear();
      _initialized = false;
      _syncDefaultCollapsed();
    }
  }

  void _syncDefaultCollapsed() {
    if (_initialized) return;
    _initialized = true;
    // Expand the first 5 files by default; collapse subsequent files on large PRs.
    if (widget.node.files.length > 5) {
      for (var i = 5; i < widget.node.files.length; i++) {
        _collapsedPaths.add(widget.node.files[i].path);
      }
    }
  }

  void _toggleFile(String path) {
    setState(() {
      if (!_collapsedPaths.remove(path)) {
        _collapsedPaths.add(path);
      }
    });
  }

  String _shortSha(String sha) {
    final trimmed = sha.trim();
    return trimmed.length > 8 ? trimmed.substring(0, 8) : trimmed;
  }

  (String, TagTone) _changeBadge(String changeKind) =>
      parseDiffChangeBadge(changeKind);

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final node = widget.node;
    final shortSha = _shortSha(node.headSha);
    final fileWord = node.filesChanged == 1 ? 'file' : 'files';

    return Container(
      key: ValueKey('diffCard:${node.id}'),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(UiTokens.of(context).card),
        border: Border.all(color: c.borderHairline),
        boxShadow: UiShadows.xs(c),
      ),
      clipBehavior: Clip.antiAlias,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Container(
            padding: const EdgeInsets.fromLTRB(14, 12, 14, 12),
            decoration: BoxDecoration(
              color: c.surfaceElevated,
              border: Border(bottom: BorderSide(color: c.borderHairline)),
            ),
            child: Wrap(
              spacing: 8,
              runSpacing: 6,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                Icon(LucideIcons.gitPullRequest, size: 15, color: c.accent),
                Text(
                  node.repo,
                  style: UiType.textStyle(
                    13.5,
                    weight: FontWeight.w600,
                    color: c.textStrong,
                  ),
                ),
                if (node.targetBranch.isNotEmpty)
                  TagPill(
                    label: node.targetBranch,
                    tone: TagTone.neutral,
                    icon: LucideIcons.gitBranch,
                  ),
                if (shortSha.isNotEmpty)
                  TagPill(
                    label: shortSha,
                    tone: TagTone.accent,
                    icon: LucideIcons.gitCommitHorizontal,
                  ),
                Text(
                  '${node.filesChanged} $fileWord',
                  style: UiType.textStyle(12, color: c.textMuted, tnum: true),
                ),
                _DiffStatChip(
                  additions: node.additions,
                  deletions: node.deletions,
                ),
              ],
            ),
          ),
          for (var i = 0; i < node.files.length; i++) ...[
            if (i > 0) Container(height: 1, color: c.borderHairline),
            _DiffFileSection(
              file: node.files[i],
              badge: _changeBadge(node.files[i].changeKind),
              expanded: !_collapsedPaths.contains(node.files[i].path),
              onToggle: () => _toggleFile(node.files[i].path),
            ),
          ],
        ],
      ),
    );
  }
}

class _DiffStatChip extends StatelessWidget {
  const _DiffStatChip({required this.additions, required this.deletions});

  final int additions;
  final int deletions;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          '+$additions',
          style: UiType.textStyle(
            12,
            weight: FontWeight.w600,
            color: c.done,
            tnum: true,
          ),
        ),
        const SizedBox(width: 6),
        Text(
          '-$deletions',
          style: UiType.textStyle(
            12,
            weight: FontWeight.w600,
            color: c.error,
            tnum: true,
          ),
        ),
      ],
    );
  }
}

class _DiffFileSection extends StatelessWidget {
  const _DiffFileSection({
    required this.file,
    required this.badge,
    required this.expanded,
    required this.onToggle,
  });

  final DiffFileNode file;
  final (String, TagTone) badge;
  final bool expanded;
  final VoidCallback onToggle;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final (badgeLabel, badgeTone) = badge;

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        MouseRegion(
          cursor: SystemMouseCursors.click,
          child: GestureDetector(
            key: ValueKey('diffFileToggle:${file.path}'),
            behavior: HitTestBehavior.opaque,
            onTap: onToggle,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
              child: Row(
                children: [
                  Icon(
                    expanded
                        ? LucideIcons.chevronDown
                        : LucideIcons.chevronRight,
                    size: 14,
                    color: c.textMuted,
                  ),
                  const SizedBox(width: 6),
                  TagPill(label: badgeLabel, tone: badgeTone),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      file.path,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontFamily: 'JetBrains Mono',
                        fontFamilyFallback: const [
                          'JetBrainsMono',
                          'Consolas',
                          'monospace',
                        ],
                        fontSize: 12.5,
                        fontWeight: FontWeight.w500,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  _DiffStatChip(
                    additions: file.additions,
                    deletions: file.deletions,
                  ),
                ],
              ),
            ),
          ),
        ),
        if (expanded && file.hunks.isNotEmpty)
          Container(
            decoration: BoxDecoration(
              border: Border(top: BorderSide(color: c.borderHairline)),
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                for (var h = 0; h < file.hunks.length; h++)
                  _DiffHunkBlock(hunk: file.hunks[h]),
              ],
            ),
          ),
      ],
    );
  }
}

class _DiffHunkBlock extends StatelessWidget {
  const _DiffHunkBlock({required this.hunk});

  final DiffHunkNode hunk;

  List<({int? oldNo, int? newNo, String line, _DiffLineKind kind})>
  _parsedLines() {
    var (oldLine, newLine) = parseDiffHunkStart(hunk.header);

    final result =
        <({int? oldNo, int? newNo, String line, _DiffLineKind kind})>[];
    for (final raw in hunk.lines) {
      if (raw.startsWith('+') && !raw.startsWith('+++')) {
        result.add((
          oldNo: null,
          newNo: newLine++,
          line: raw,
          kind: _DiffLineKind.added,
        ));
      } else if (raw.startsWith('-') && !raw.startsWith('---')) {
        result.add((
          oldNo: oldLine++,
          newNo: null,
          line: raw,
          kind: _DiffLineKind.deleted,
        ));
      } else if (raw.startsWith(r'\')) {
        result.add((
          oldNo: null,
          newNo: null,
          line: raw,
          kind: _DiffLineKind.meta,
        ));
      } else {
        result.add((
          oldNo: oldLine++,
          newNo: newLine++,
          line: raw,
          kind: _DiffLineKind.context,
        ));
      }
    }
    return result;
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final rows = _parsedLines();
    const codeBase = TextStyle(
      fontFamily: 'JetBrains Mono',
      fontFamilyFallback: ['JetBrainsMono', 'Consolas', 'monospace'],
      fontSize: 12,
      height: 1.45,
      fontFeatures: UiType.tabular,
    );

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (hunk.header.isNotEmpty)
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
            color: c.surfaceSunk,
            child: Text(
              hunk.header,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: codeBase.copyWith(fontSize: 11.5, color: c.textMuted),
            ),
          ),
        for (final row in rows) _buildRow(c, codeBase, row),
      ],
    );
  }

  Widget _buildRow(
    UiColors c,
    TextStyle codeBase,
    ({int? oldNo, int? newNo, String line, _DiffLineKind kind}) row,
  ) {
    final (bg, fg, barColor) = switch (row.kind) {
      _DiffLineKind.added => (
        c.doneBg.withValues(alpha: c.isDark ? 0.45 : 0.7),
        c.done,
        c.done,
      ),
      _DiffLineKind.deleted => (
        c.errorBg.withValues(alpha: c.isDark ? 0.45 : 0.7),
        c.error,
        c.error,
      ),
      _DiffLineKind.meta => (c.surfaceSunk, c.textSubtle, c.borderHairline),
      _DiffLineKind.context => (null, c.textBody, null),
    };

    return Container(
      decoration: BoxDecoration(
        color: bg,
        border: barColor != null
            ? Border(left: BorderSide(color: barColor, width: 2.5))
            : null,
      ),
      padding: EdgeInsets.only(
        left: barColor != null ? 7.5 : 10,
        right: 12,
        top: 1.5,
        bottom: 1.5,
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 30,
            child: Text(
              row.oldNo?.toString() ?? '',
              textAlign: TextAlign.right,
              style: codeBase.copyWith(fontSize: 11, color: c.textSubtle),
            ),
          ),
          const SizedBox(width: 6),
          SizedBox(
            width: 30,
            child: Text(
              row.newNo?.toString() ?? '',
              textAlign: TextAlign.right,
              style: codeBase.copyWith(fontSize: 11, color: c.textSubtle),
            ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Text(row.line, style: codeBase.copyWith(color: fg)),
          ),
        ],
      ),
    );
  }
}

enum _DiffLineKind { context, added, deleted, meta }
