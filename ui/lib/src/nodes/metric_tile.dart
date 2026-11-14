import 'dart:math' as math;

import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/app_tile.dart';
import '../design/controls.dart';
import '../design/tokens.dart';
import 'button.dart';
import 'node_common.dart';
import 'node_context.dart';

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
    final c = IcTheme.of(context);
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
        borderRadius: BorderRadius.circular(IcRadius.card),
        border: Border.all(color: c.borderHairline),
        boxShadow: IcShadow.xs(c),
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
                  style: IcType.text(
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
                style: IcType.text(
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
                  style: IcType.text(
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
    IcColors c,
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
                      style: IcType.text(
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
                  style: IcType.text(
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
