import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flame/components.dart';
import 'package:flame/flame.dart';
import 'package:flame/particles.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

import 'graph_semantics.dart';
import 'node_components.dart';

final class CircleNode extends NodeComponent {
  CircleNode(super.node);

  @override
  void changed() {
    final radius = param('radius', 24);
    size.setAll(radius * 2);
    super.changed();
  }

  @override
  void render(ui.Canvas canvas) =>
      canvas.drawCircle((size / 2).toOffset(), size.x / 2, paint);
}

final class RectNode extends NodeComponent {
  RectNode(super.node);

  @override
  void changed() {
    size.setValues(param('width', 48), param('height', 48));
    super.changed();
  }

  @override
  void render(ui.Canvas canvas) {
    final radius = param('radius', 0);
    canvas.drawRRect(
      ui.RRect.fromRectAndRadius(size.toRect(), ui.Radius.circular(radius)),
      paint,
    );
  }
}

final class TextNode extends NodeComponent {
  TextNode(super.node);
  TextPainter? _painter;

  @override
  void changed() {
    super.changed();
    _painter = TextPainter(
      text: TextSpan(
        text: params['text'] ?? '',
        style: TextStyle(color: color, fontSize: param('size', 16)),
      ),
      textDirection: TextDirection.ltr,
    )..layout();
    size.setValues(_painter!.width, _painter!.height);
  }

  // Text draws through a layer so opacity and OpacityEffect fade it like the shapes.
  @override
  void render(ui.Canvas canvas) {
    final painter = _painter;
    if (painter == null) return;
    canvas.saveLayer(size.toRect(), ui.Paint()..color = paint.color);
    painter.paint(canvas, Offset.zero);
    canvas.restore();
  }
}

final class SpriteNode extends NodeComponent {
  SpriteNode(super.node);
  ui.Image? _image;
  String? _loading;

  @override
  void changed() {
    size.setValues(param('width', 48), param('height', 48));
    super.changed();
    final src = params['src'];
    if (src != null && src != _loading) {
      _loading = src;
      _image = null;
      Flame.images
          .load(src)
          .then((image) {
            if (_loading == src) _image = image;
            if (isMounted) game.wake();
          })
          .catchError((Object _) => _image = null);
    }
  }

  @override
  void render(ui.Canvas canvas) {
    final image = _image;
    if (image == null) {
      // A failed or pending load draws as a rect so the layout holds.
      canvas.drawRect(size.toRect(), paint);
      return;
    }
    canvas.drawImageRect(
      image,
      ui.Rect.fromLTWH(0, 0, image.width.toDouble(), image.height.toDouble()),
      size.toRect(),
      paint,
    );
  }
}

/// `d` is a move/line/cubic list: `M x y L x y C x1 y1 x2 y2 x y Z`.
/// Supports the 3 Accordant DDD edge styles (`accepted`, `candidate`, `refused`),
/// directional arrowheads (`───▶`), flowing signal pulse dots, and `✕ Rule` guard badges.
final class PathNode extends NodeComponent {
  PathNode(super.node);

  ui.Path _path = ui.Path();
  AccordantEdgeStyle? _edgeStyle;
  bool _drawArrow = false;
  bool _activePulse = false;
  String _badgeLabel = '';
  TextPainter? _badgePainter;
  double _pulseElapsed = 0;
  static const double _pulseDuration = 0.85;

  @visibleForTesting
  AccordantEdgeStyle? get accordantStyle => _edgeStyle;

  @visibleForTesting
  String get badgeLabel => _badgeLabel;

  @visibleForTesting
  bool get hasDirectionalArrow => _drawArrow;

  bool get needsPulseFrames =>
      _activePulse &&
      isMounted &&
      game.animations &&
      _pulseElapsed < _pulseDuration;

  @override
  void changed() {
    final raw = parse(params['d'] ?? '');
    final bounds = raw.getBounds();
    _edgeStyle = switch (params['edgeStyle']) {
      'accepted' => AccordantEdgeStyle.accepted,
      'candidate' => AccordantEdgeStyle.candidate,
      'refused' => AccordantEdgeStyle.refused,
      _ => null,
    };
    _drawArrow = params['arrow'] == 'true' || _edgeStyle != null;
    _activePulse = params['activePulse'] == 'true';
    _badgeLabel = params['badge'] ?? '';
    _pulseElapsed = 0;

    if (_edgeStyle != null || _drawArrow) {
      final boxW = math.max(bounds.width, 28.0);
      final boxH = math.max(bounds.height, 28.0);
      size.setValues(boxW, boxH);
      _path = raw.shift(
        Offset(boxW / 2 - bounds.center.dx, boxH / 2 - bounds.center.dy),
      );
    } else if (bounds.left < 0 || bounds.top < 0) {
      final dx = bounds.left < 0 ? -bounds.left : 0.0;
      final dy = bounds.top < 0 ? -bounds.top : 0.0;
      _path = raw.shift(Offset(dx, dy));
      size.setValues(bounds.right + dx, bounds.bottom + dy);
    } else {
      _path = raw;
      size.setValues(bounds.right, bounds.bottom);
    }

    if (_badgeLabel.isNotEmpty) {
      _badgePainter = TextPainter(
        text: TextSpan(
          text: _badgeLabel,
          style: const TextStyle(
            color: Color(0xFFFFE4E6),
            fontSize: 9.5,
            fontWeight: FontWeight.w700,
          ),
        ),
        textDirection: TextDirection.ltr,
        maxLines: 1,
        ellipsis: '…',
      )..layout(maxWidth: 164);
    } else {
      _badgePainter = null;
    }

    super.changed();
    paint
      ..style = params['fill'] == 'true'
          ? ui.PaintingStyle.fill
          : ui.PaintingStyle.stroke
      ..strokeCap = ui.StrokeCap.round
      ..strokeJoin = ui.StrokeJoin.round
      ..strokeWidth = param('stroke', 2);
  }

  @override
  void update(double dt) {
    super.update(dt);
    if (_activePulse && game.animations && _pulseElapsed < _pulseDuration) {
      _pulseElapsed = (_pulseElapsed + dt).clamp(0.0, _pulseDuration);
    }
  }

  static ui.Path parse(String d) {
    final path = ui.Path();
    final tokens = d
        .replaceAllMapped(RegExp('[MLCZmlcz]'), (m) => ' ${m[0]} ')
        .split(RegExp(r'[\s,]+'))
        .where((t) => t.isNotEmpty)
        .toList();
    var command = 'M';
    for (var i = 0; i < tokens.length;) {
      final token = tokens[i];
      if (RegExp('^[MLCZmlcz]\$').hasMatch(token)) {
        command = token.toUpperCase();
        i++;
        if (command == 'Z') path.close();
        continue;
      }
      if (command == 'C') {
        if (i + 5 >= tokens.length) break;
        final x1 = double.tryParse(token) ?? 0;
        final y1 = double.tryParse(tokens[i + 1]) ?? 0;
        final x2 = double.tryParse(tokens[i + 2]) ?? 0;
        final y2 = double.tryParse(tokens[i + 3]) ?? 0;
        final x3 = double.tryParse(tokens[i + 4]) ?? 0;
        final y3 = double.tryParse(tokens[i + 5]) ?? 0;
        path.cubicTo(x1, y1, x2, y2, x3, y3);
        i += 6;
        continue;
      }
      if (i + 1 >= tokens.length) break;
      final x = double.tryParse(token) ?? 0;
      final y = double.tryParse(tokens[i + 1]) ?? 0;
      if (command == 'M') {
        path.moveTo(x, y);
        command = 'L';
      } else {
        path.lineTo(x, y);
      }
      i += 2;
    }
    return path;
  }

  static ui.Path _segmentPattern(
    ui.PathMetric metric,
    double dash,
    double gap,
  ) {
    final out = ui.Path();
    var dist = 0.0;
    final total = metric.length;
    while (dist < total) {
      final end = math.min(dist + dash, total);
      out.addPath(metric.extractPath(dist, end), Offset.zero);
      dist += dash + gap;
    }
    return out;
  }

  static void _drawArrowhead(
    ui.Canvas canvas,
    ui.Tangent endTangent,
    ui.Color arrowColor,
  ) {
    final tip = endTangent.position;
    final dir = endTangent.vector;
    final len = dir.distance;
    if (len < 1e-4) return;
    final ux = dir.dx / len;
    final uy = dir.dy / len;
    const arrowLength = 9.5;
    const arrowHalfW = 4.6;
    final baseX = tip.dx - ux * arrowLength;
    final baseY = tip.dy - uy * arrowLength;
    final nx = -uy;
    final ny = ux;

    final arrow = ui.Path()
      ..moveTo(tip.dx, tip.dy)
      ..lineTo(baseX + nx * arrowHalfW, baseY + ny * arrowHalfW)
      ..lineTo(baseX - nx * arrowHalfW, baseY - ny * arrowHalfW)
      ..close();
    canvas.drawPath(arrow, ui.Paint()..color = arrowColor);
  }

  @override
  void render(ui.Canvas canvas) {
    final edgeStyle = _edgeStyle;
    if (edgeStyle == null && !_drawArrow) {
      canvas.drawPath(_path, paint);
      return;
    }

    final strokeColor = paint.color;
    final strokeW = paint.strokeWidth;
    final metrics = _path.computeMetrics().toList(growable: false);
    if (metrics.isEmpty) return;

    for (final metric in metrics) {
      if (metric.length < 0.5) continue;
      switch (edgeStyle ?? AccordantEdgeStyle.accepted) {
        case AccordantEdgeStyle.accepted:
          canvas.drawPath(
            metric.extractPath(0, metric.length),
            ui.Paint()
              ..style = ui.PaintingStyle.stroke
              ..strokeCap = ui.StrokeCap.round
              ..strokeWidth = strokeW + 3.6
              ..color = strokeColor.withValues(
                alpha: (strokeColor.a * 0.22).clamp(0.0, 1.0),
              ),
          );
          canvas.drawPath(_path, paint);
        case AccordantEdgeStyle.candidate:
          canvas.drawPath(_segmentPattern(metric, 8.0, 6.0), paint);
        case AccordantEdgeStyle.refused:
          canvas.drawPath(_segmentPattern(metric, 2.2, 5.4), paint);
      }

      if (_drawArrow) {
        final endTangent = metric.getTangentForOffset(metric.length);
        if (endTangent != null) {
          _drawArrowhead(canvas, endTangent, strokeColor);
        }
      }

      if (_activePulse &&
          (edgeStyle == null || edgeStyle == AccordantEdgeStyle.accepted)) {
        final phase = game.animations && _pulseElapsed < _pulseDuration
            ? (_pulseElapsed / _pulseDuration).clamp(0.08, 0.92)
            : 0.68;
        final pulseTangent = metric.getTangentForOffset(metric.length * phase);
        if (pulseTangent != null) {
          final p = pulseTangent.position;
          canvas.drawCircle(
            p,
            7.0,
            ui.Paint()
              ..color = strokeColor.withValues(
                alpha: (strokeColor.a * 0.28).clamp(0.0, 1.0),
              ),
          );
          canvas.drawCircle(
            p,
            3.6,
            ui.Paint()
              ..color = const Color(0xFFE0F2FE)
                  .withValues(alpha: strokeColor.a.clamp(0.0, 1.0)),
          );
        }
      }
    }

    if (_badgePainter case final badge?) {
      final firstMetric = metrics.first;
      final midTangent = firstMetric.getTangentForOffset(
        firstMetric.length * 0.5,
      );
      if (midTangent != null) {
        final mid = midTangent.position;
        final pillW = badge.width + 12;
        final pillH = badge.height + 5;
        final rect = Rect.fromCenter(center: mid, width: pillW, height: pillH);
        final rrect = ui.RRect.fromRectAndRadius(
          rect,
          const ui.Radius.circular(8),
        );
        canvas.drawRRect(rrect, ui.Paint()..color = const Color(0xEE2B0E14));
        canvas.drawRRect(
          rrect,
          ui.Paint()
            ..style = ui.PaintingStyle.stroke
            ..strokeWidth = 1.1
            ..color = strokeColor,
        );
        badge.paint(
          canvas,
          Offset(mid.dx - badge.width / 2, mid.dy - badge.height / 2),
        );
      }
    }
  }
}

final class ParticlesNode extends NodeComponent {
  ParticlesNode(super.node);
  final _random = math.Random();

  @override
  void changed() {
    size.setAll(param('radius', 48) * 2);
    super.changed();
  }

  @override
  void update(double dt) {
    super.update(dt);
    if (children.whereType<ParticleSystemComponent>().isEmpty) {
      add(ParticleSystemComponent(position: size / 2, particle: _burst()));
    }
  }

  Particle _burst() {
    final speed = param('speed', 60);
    final dot = ui.Paint()..color = sceneColor(params['color']) ?? color;
    return Particle.generate(
      count: param('count', 24).round(),
      lifespan: 1.5,
      generator: (_) {
        final direction = _random.nextDouble() * 2 * math.pi;
        return AcceleratedParticle(
          speed:
              Vector2(math.cos(direction), math.sin(direction)) *
              speed *
              (0.5 + _random.nextDouble()),
          child: CircleParticle(radius: param('dot', 2), paint: dot),
        );
      },
    );
  }

  @override
  void render(ui.Canvas canvas) {}
}

final class GroupNode extends NodeComponent {
  GroupNode(super.node);

  @override
  void render(ui.Canvas canvas) {}
}
