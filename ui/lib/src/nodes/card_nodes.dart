import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/app_tile.dart';
import '../design/cards/approval_card.dart';
import '../design/cards/choice_card.dart';
import '../design/cards/draft_card.dart';
import '../design/cards/job_card.dart';
import '../design/cards/record_card.dart';
import '../design/cards/secret_card.dart';
import '../primitives/display_primitives.dart';
import '../primitives/layout_primitives.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import '../theme/wire_node_values.dart';
import 'action_nodes.dart';
import 'node_common.dart';
import 'node_context.dart';

// --- src/nodes/approval_card.dart ---
Widget renderApprovalCard(ApprovalCardNode node, NodeContext ctx) {
  final resolve = node.resolve;
  final canResolve = resolve != null && ctx.confirmation && ctx.interactive;
  return Semantics(
    container: true,
    explicitChildNodes: true,
    label: controlNameOf(node.automationName, node.title, node.key),
    child: ApprovalCard(
      key: ValueKey('approval:${node.id}'),
      source: cardSource(
        node.systemName,
        iconKey: node.icon,
        capabilityLabel: node.capabilityLabel,
        approvalFallbackShield: true,
      ),
      readOnly: !canResolve,
      data: ApprovalData(
        title: node.title,
        summary: node.summary,
        rows: [
          for (final row in node.rows)
            ApprovalRow(
              label: row.label,
              oldValue: row.oldValue,
              newValue: row.newValue,
              amount: row.amount,
              warn: row.warn,
            ),
        ],
        valueLabel: node.valueLabel,
        systemName: node.systemName,
      ),
      state: parseApprovalState(node.status),
      onApprove: canResolve
          ? () => ctx.resolve(resolve.planId, approve: true)
          : null,
      onReject: canResolve
          ? () => ctx.resolve(resolve.planId, approve: false)
          : null,
    ),
  );
}

// --- src/nodes/choice_card.dart ---

Widget renderChoiceCard(ChoiceCardNode node, NodeContext ctx) => ChoiceCard(
  source: cardSource('Ino'),
  question: node.question,
  options: [
    for (final option in node.options)
      ChoiceOption(option.title, detail: option.detail),
  ],
  picked: node.pickedIndex,
  onPick: ctx.interactive && node.pickedIndex == null
      ? (i) {
          final pick = node.options.elementAtOrNull(i)?.pick;
          if (pick != null && pick.enabled) {
            pressCommand(pick.key, pick.command, ctx, null);
          }
        }
      : null,
);

// --- src/nodes/draft_card.dart ---

Widget renderDraftCard(DraftCardNode node, NodeContext ctx) => DraftCard(
  key: ValueKey('draft:${node.id}'),
  source: cardSource(node.channel),
  data: DraftData(
    channel: parseDraftChannel(node.channel),
    to: node.to,
    body: node.body,
    from: node.from,
    subject: node.subject,
  ),
  state: parseDraftState(node.status),
  onSend: pressOf(node.send, ctx),
  onDiscard: pressOf(node.discard, ctx),
);

// --- src/nodes/record_card.dart ---

Widget renderRecordCard(RecordCardNode node, NodeContext ctx) => RecordCard(
  source: cardSource(node.objectLabel),
  data: RecordData(
    objectLabel: node.objectLabel,
    name: node.name,
    subtitle: node.subtitle,
    linkLabel: node.linkLabel,
    fields: [
      for (final field in node.fields)
        RecordField(field.label, field.value, warn: field.warn),
    ],
  ),
  onOpen: pressOf(node.open, ctx),
);

// --- src/nodes/secret_card.dart ---

Widget renderSecretCard(SecretCardNode node, NodeContext ctx) => SecretCard(
  source: cardSource(node.savedIn),
  data: SecretRequestData(
    name: node.name,
    why: node.why,
    savedIn: node.savedIn,
  ),
  state: parseSecretState(node.status),
  onEnter: pressOf(node.enter, ctx),
  onSkip: pressOf(node.skip, ctx),
);

// --- src/nodes/job_card.dart ---

Widget renderJobCard(JobCardNode node, NodeContext ctx) => JobCard(
  source: cardSource('Job'),
  data: JobData(
    title: node.title,
    progress: node.progress > 1 ? node.progress / 100 : node.progress,
    resultText: node.resultText,
    steps: [
      for (final step in node.steps)
        JobStep(
          step.label,
          detail: step.detail,
          status: parseJobStepStatus(step.status),
        ),
    ],
  ),
  state: parseJobState(node.status),
  onCancel: pressOf(node.cancel, ctx),
  onOpenArtifact: pressOf(node.openArtifact, ctx),
  openArtifactLabel: node.openArtifact?.label ?? 'Open artifact',
);

// --- src/nodes/stat_card.dart ---
Widget renderStatCard(StatCardNode node, NodeContext ctx) => Semantics(
  container: true,
  explicitChildNodes: true,
  label: controlNameOf(node.automationName, node.label, node.key),
  child: Builder(
    builder: (context) {
      final text = Theme.of(context).textTheme;
      return UiMixSurface(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: 4,
          children: [
            Text(node.label, style: text.bodySmall),
            Text(
              node.value,
              style: UiType.displayStyle(
                28,
                weight: FontWeight.w800,
                color: text.headlineSmall?.color,
              ).copyWith(fontFeatures: UiType.tabular),
            ),
            if (node.delta case final delta?)
              UiBadge.fromWire(
                label: delta,
                tone: switch (node.tone) {
                  BadgeTone.accent => UiBadgeTone.accent,
                  BadgeTone.done => UiBadgeTone.success,
                  BadgeTone.working => UiBadgeTone.warning,
                  BadgeTone.error => UiBadgeTone.danger,
                  BadgeTone.neutral => UiBadgeTone.neutral,
                },
              ),
          ],
        ),
      );
    },
  ),
);

// --- src/nodes/metric_tile.dart ---
Widget renderMetricTile(MetricTileNode node, NodeContext ctx) =>
    MetricTileView(node: node, ctx: ctx);

/// Live workspace KPI tile with tabular metric value, directional delta pill,
/// status badge, smooth Canvas sparkline + dashed alert threshold rule, and actions.
class MetricTileView extends StatefulWidget {
  const MetricTileView({super.key, required this.node, required this.ctx});

  final MetricTileNode node;
  final NodeContext ctx;

  @override
  State<MetricTileView> createState() => _MetricTileViewState();
}

class _MetricTileViewState extends State<MetricTileView> {
  int? _hoverIndex;

  TagTone _tagTone(String wireTone) => switch (nodeToneOf(wireTone)) {
    UiNodeTone.done => TagTone.done,
    UiNodeTone.working => TagTone.working,
    UiNodeTone.error => TagTone.error,
    UiNodeTone.accent => TagTone.accent,
    UiNodeTone.neutral => TagTone.neutral,
  };

  IconData? _statusIcon(TagTone tone) => switch (tone) {
    TagTone.done => LucideIcons.circleCheck,
    TagTone.working => LucideIcons.activity,
    TagTone.error => LucideIcons.triangleAlert,
    TagTone.accent => LucideIcons.bellRing,
    TagTone.neutral => null,
  };

  IconData _deltaIcon(String delta, TagTone tone) {
    final trimmed = delta.trimLeft();
    if (trimmed.startsWith('+') || trimmed.startsWith('▲')) {
      return LucideIcons.trendingUp;
    }
    if (trimmed.startsWith('-') ||
        trimmed.startsWith('−') ||
        trimmed.startsWith('▼')) {
      return LucideIcons.trendingDown;
    }
    return switch (tone) {
      TagTone.done => LucideIcons.trendingUp,
      TagTone.error => LucideIcons.trendingDown,
      _ => LucideIcons.minus,
    };
  }

  String _formatHoverValue(double v) {
    if (v.abs() >= 1000) {
      return v.toStringAsFixed(v == v.roundToDouble() ? 0 : 2);
    }
    return v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(2);
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final node = widget.node;
    final statusLabel = node.statusLabel;
    final statusTone = _tagTone(node.statusTone);
    final delta = node.delta;
    final deltaTone = _tagTone(node.deltaTone);
    final points = node.sparkline;
    final hasSparkline = points != null && points.isNotEmpty;
    final actions = node.actions ?? const <ButtonNode>[];

    final lineColor = switch (statusTone) {
      TagTone.done => c.done,
      TagTone.working => c.working,
      TagTone.error => c.error,
      TagTone.accent || TagTone.neutral => c.accent,
    };

    return Container(
      key: ValueKey('metricTile:${node.id}'),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(UiTokens.of(context).card),
        border: Border.all(color: c.borderHairline),
        boxShadow: UiShadows.xs(c),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              IcAppIcon(
                appId: node.appId.isEmpty ? 'workspace' : node.appId,
                size: 22,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  node.title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: UiType.textStyle(
                    12.5,
                    weight: FontWeight.w600,
                    color: c.textMuted,
                  ),
                ),
              ),
              if (statusLabel != null && statusLabel.isNotEmpty) ...[
                const SizedBox(width: 8),
                TagPill(
                  label: statusLabel,
                  tone: statusTone,
                  icon: _statusIcon(statusTone),
                ),
              ],
            ],
          ),
          const SizedBox(height: 12),
          Wrap(
            spacing: 8,
            runSpacing: 6,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text(
                node.value,
                style: UiType.textStyle(
                  26,
                  weight: FontWeight.w700,
                  color: c.textStrong,
                  tnum: true,
                ),
              ),
              if (delta != null && delta.isNotEmpty)
                TagPill(
                  label: delta,
                  tone: deltaTone,
                  icon: _deltaIcon(delta, deltaTone),
                ),
            ],
          ),
          if (hasSparkline) ...[
            const SizedBox(height: 12),
            _buildSparklineArea(c, points, node.threshold, lineColor),
          ] else if (node.threshold != null) ...[
            const SizedBox(height: 8),
            Row(
              children: [
                Icon(LucideIcons.bell, size: 12, color: c.working),
                const SizedBox(width: 5),
                Text(
                  'Threshold ${_formatHoverValue(node.threshold!)}',
                  style: UiType.textStyle(
                    11.5,
                    weight: FontWeight.w500,
                    color: c.textMuted,
                    tnum: true,
                  ),
                ),
              ],
            ),
          ],
          if (actions.isNotEmpty) ...[
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final btn in actions)
                  renderButton(btn, widget.ctx.deeper()),
              ],
            ),
          ],
        ],
      ),
    );
  }

  Widget _buildSparklineArea(
    UiColors c,
    List<double> points,
    double? threshold,
    Color lineColor,
  ) {
    final activeIdx = _hoverIndex != null && _hoverIndex! < points.length
        ? _hoverIndex
        : null;

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        LayoutBuilder(
          builder: (context, constraints) {
            final width = constraints.maxWidth.isFinite
                ? constraints.maxWidth
                : 240.0;
            return MouseRegion(
              onHover: (e) {
                if (points.length < 2 || width <= 0) return;
                final ratio = (e.localPosition.dx / width).clamp(0.0, 1.0);
                final idx = (ratio * (points.length - 1)).round();
                if (idx != _hoverIndex) {
                  setState(() => _hoverIndex = idx);
                }
              },
              onExit: (_) {
                if (_hoverIndex != null) {
                  setState(() => _hoverIndex = null);
                }
              },
              child: SizedBox(
                height: 48,
                width: width,
                child: CustomPaint(
                  key: ValueKey('sparkline:${widget.node.id}'),
                  painter: SparklinePainter(
                    points: points,
                    threshold: threshold,
                    lineColor: lineColor,
                    thresholdColor: c.working,
                    baselineColor: c.borderHairline,
                    hoverIndex: activeIdx,
                    dotBorderColor: c.surfaceCard,
                  ),
                ),
              ),
            );
          },
        ),
        if (threshold != null || activeIdx != null) ...[
          const SizedBox(height: 6),
          Row(
            children: [
              if (threshold != null)
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Container(
                      width: 10,
                      height: 2,
                      color: c.working.withValues(alpha: 0.85),
                    ),
                    const SizedBox(width: 5),
                    Text(
                      'Threshold ${_formatHoverValue(threshold)}',
                      style: UiType.textStyle(
                        11.5,
                        weight: FontWeight.w500,
                        color: c.textMuted,
                        tnum: true,
                      ),
                    ),
                  ],
                ),
              const Spacer(),
              if (activeIdx != null)
                Text(
                  _formatHoverValue(points[activeIdx]),
                  style: UiType.textStyle(
                    11.5,
                    weight: FontWeight.w600,
                    color: c.textStrong,
                    tnum: true,
                  ),
                ),
            ],
          ),
        ],
      ],
    );
  }
}

/// Custom painter for `MetricTileView`'s smooth sparkline + area fill
/// + optional horizontal dashed threshold line.
class SparklinePainter extends CustomPainter {
  const SparklinePainter({
    required this.points,
    required this.lineColor,
    required this.thresholdColor,
    required this.baselineColor,
    required this.dotBorderColor,
    this.threshold,
    this.hoverIndex,
  });

  final List<double> points;
  final double? threshold;
  final Color lineColor;
  final Color thresholdColor;
  final Color baselineColor;
  final Color dotBorderColor;
  final int? hoverIndex;

  @override
  void paint(Canvas canvas, Size size) {
    if (points.isEmpty || size.width <= 0 || size.height <= 0) return;

    const padY = 4.0;
    final usableHeight = math.max(1.0, size.height - padY * 2);

    var minVal = points.reduce(math.min);
    var maxVal = points.reduce(math.max);
    if (threshold != null) {
      minVal = math.min(minVal, threshold!);
      maxVal = math.max(maxVal, threshold!);
    }
    final span = (maxVal - minVal).abs() < 1e-9 ? 1.0 : (maxVal - minVal);

    double yFor(double value) {
      final normalized = (value - minVal) / span;
      return padY + usableHeight * (1.0 - normalized.clamp(0.0, 1.0));
    }

    double xFor(int index) {
      if (points.length == 1) return size.width / 2;
      return (index / (points.length - 1)) * size.width;
    }

    // Optional dashed threshold rule.
    if (threshold != null) {
      final ty = yFor(threshold!);
      final dashPaint = Paint()
        ..color = thresholdColor.withValues(alpha: 0.78)
        ..strokeWidth = 1.25
        ..style = PaintingStyle.stroke;
      const dashWidth = 4.0;
      const dashGap = 3.0;
      var x = 0.0;
      while (x < size.width) {
        canvas.drawLine(
          Offset(x, ty),
          Offset(math.min(x + dashWidth, size.width), ty),
          dashPaint,
        );
        x += dashWidth + dashGap;
      }
    }

    if (points.length == 1) {
      final center = Offset(xFor(0), yFor(points.first));
      canvas.drawCircle(center, 3.5, Paint()..color = lineColor);
      return;
    }

    final coords = [
      for (var i = 0; i < points.length; i++) Offset(xFor(i), yFor(points[i])),
    ];

    final linePath = Path()..moveTo(coords.first.dx, coords.first.dy);
    for (var i = 0; i < coords.length - 1; i++) {
      final p0 = coords[i];
      final p1 = coords[i + 1];
      final midX = (p0.dx + p1.dx) / 2;
      linePath.cubicTo(midX, p0.dy, midX, p1.dy, p1.dx, p1.dy);
    }

    final fillPath = Path.from(linePath)
      ..lineTo(coords.last.dx, size.height)
      ..lineTo(coords.first.dx, size.height)
      ..close();

    final fillPaint = Paint()
      ..shader = LinearGradient(
        begin: Alignment.topCenter,
        end: Alignment.bottomCenter,
        colors: [
          lineColor.withValues(alpha: 0.24),
          lineColor.withValues(alpha: 0.0),
        ],
      ).createShader(Offset.zero & size);
    canvas.drawPath(fillPath, fillPaint);

    final strokePaint = Paint()
      ..color = lineColor
      ..strokeWidth = 2.0
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    canvas.drawPath(linePath, strokePaint);

    final active =
        hoverIndex != null && hoverIndex! >= 0 && hoverIndex! < coords.length
        ? coords[hoverIndex!]
        : coords.last;

    if (hoverIndex != null) {
      final guidePaint = Paint()
        ..color = baselineColor
        ..strokeWidth = 1.0;
      canvas.drawLine(
        Offset(active.dx, 0),
        Offset(active.dx, size.height),
        guidePaint,
      );
    }

    canvas.drawCircle(active, 4.0, Paint()..color = dotBorderColor);
    canvas.drawCircle(active, 2.8, Paint()..color = lineColor);
  }

  @override
  bool shouldRepaint(covariant SparklinePainter oldDelegate) =>
      oldDelegate.points != points ||
      oldDelegate.threshold != threshold ||
      oldDelegate.lineColor != lineColor ||
      oldDelegate.thresholdColor != thresholdColor ||
      oldDelegate.hoverIndex != hoverIndex;
}

// --- src/nodes/diff_card.dart ---
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
