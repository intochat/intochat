import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flame/components.dart';
import 'package:flame/effects.dart';
import 'package:flame_behavior_tree/flame_behavior_tree.dart';
import 'package:flutter/painting.dart';
import 'package:flutter_svg/flutter_svg.dart' as svg;
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../../theme/shell_icon.dart';
import '../behavior/default_idle_tree.dart';
import '../behavior/tree_runtime.dart';
import '../graph_semantics.dart';
import '../node_components.dart';

typedef SphereIconLoader = Future<Uint8List?> Function(
  String neuron,
  String icon,
);

/// What a sphere's behavior tree moves: draws a sleek 3D-elevated rounded icon card, status ring,
/// optional `0–100%` circular progress arc, and labels, running the tree's moveTo/scale/fade/pulse effects.
final class SphereVisual extends PositionComponent
    with HasPaint
    implements SphereBody {
  SphereVisual() : super(anchor: Anchor.center);

  @override
  double now = 0;
  @override
  int frame = 0;

  /// Off under reduced motion: tasks still run their clock but draw nothing.
  bool animations = true;
  String icon = '';
  String title = '';
  String description = '';
  String nodeStatus = '';
  Color accentColor = const Color(0xFF7FB069);
  double? progress;
  bool selected = false;
  bool badge = false;
  double presentationOpacity = 1;
  ui.Image? iconImage;

  final _effects = <String, Effect>{};
  TextPainter? _titleText, _descriptionText, _progressText;
  String _cachedProgressLabel = '';

  bool get hasEffects => _effects.values.any((effect) => !effect.isRemoved);

  void advance(double dt) {
    now += dt;
    frame++;
  }

  void setLabels(String title, String description) {
    if (title != this.title || _titleText == null) {
      this.title = title;
      _titleText = _label(
        title,
        13,
        const Color(0xFFE6EDF3),
        weight: FontWeight.w600,
      );
    }
    if (description != this.description || _descriptionText == null) {
      this.description = description;
      _descriptionText = _label(description, 11, const Color(0xFF9AA4B2));
    }
  }

  void setProgress(double? value) {
    progress = value?.clamp(0.0, 1.0);
    if (progress == null) {
      _progressText = null;
      _cachedProgressLabel = '';
      return;
    }
    final pctText = '${(progress! * 100).round()}%';
    if (pctText != _cachedProgressLabel || _progressText == null) {
      _cachedProgressLabel = pctText;
      _progressText = TextPainter(
        text: TextSpan(
          text: pctText,
          style: const TextStyle(
            color: Color(0xFFF1F5F9),
            fontSize: 9.5,
            fontWeight: FontWeight.w700,
          ),
        ),
        textDirection: TextDirection.ltr,
        maxLines: 1,
      )..layout();
    }
  }

  static TextPainter _label(
    String text,
    double size,
    Color color, {
    FontWeight weight = FontWeight.w400,
  }) => TextPainter(
    text: TextSpan(
      text: text,
      style: TextStyle(color: color, fontSize: size, fontWeight: weight),
    ),
    textDirection: TextDirection.ltr,
    maxLines: 1,
    ellipsis: '...',
  )..layout(maxWidth: 176);

  @override
  void begin(String kind, TreeNode params) {
    if (!animations) return;
    final controller = EffectController(
      duration: math.max(params.seconds, 0.001),
    );
    final (property, effect) = switch (kind) {
      'moveTo' => (
        'position',
        MoveEffect.to(
          Vector2(size.x / 2 + params.x, size.y / 2 + params.y),
          controller,
        ),
      ),
      'scale' => (
        'scale',
        ScaleEffect.to(Vector2.all(params.factor), controller),
      ),
      'fade' => ('opacity', OpacityEffect.to(params.alpha, controller)),
      'pulse' => (
        'scale',
        ScaleEffect.to(
          Vector2.all(1 + params.amplitude),
          EffectController(
            duration: math.max(params.seconds, 0.002) / 2,
            alternate: true,
          ),
        ),
      ),
      _ => (null, null),
    };
    if (property == null || effect == null) return;
    _effects.remove(property)?.removeFromParent();
    _effects[property] = effect;
    add(effect);
  }

  @override
  void render(ui.Canvas canvas) {
    // HasPaint stores effect opacity in paint.color; render must leave it intact.
    canvas.saveLayer(
      null,
      ui.Paint()
        ..color = Color.fromRGBO(255, 255, 255, opacity * presentationOpacity),
    );

    final center = (size / 2).toOffset();
    final radius = size.x / 2;
    final cardRect = Rect.fromLTWH(4, 4, size.x - 8, size.y - 8);
    const cardRadius = ui.Radius.circular(22);
    final cardRRect = ui.RRect.fromRectAndRadius(cardRect, cardRadius);

    // 1. 3D Extruded Pedestal Lip
    final pedestalRRect = ui.RRect.fromRectAndRadius(
      cardRect.shift(const Offset(0, 4.5)),
      cardRadius,
    );
    canvas.drawRRect(
      pedestalRRect,
      ui.Paint()..color = const Color(0xFF060A10),
    );
    canvas.drawRRect(
      pedestalRRect,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 1.2
        ..color = Color.lerp(const Color(0xFF0E1622), accentColor, 0.30)!,
    );

    // 2. 3D Elevated Card Surface + Top Bevel Highlight
    final topSurface = Color.lerp(const Color(0xFF1C2736), accentColor, 0.22)!;
    const bottomSurface = Color(0xFF0F1620);
    canvas.drawRRect(
      cardRRect,
      ui.Paint()
        ..shader = ui.Gradient.linear(
          cardRect.topCenter,
          cardRect.bottomCenter,
          [topSurface, bottomSurface],
        ),
    );
    canvas.drawRRect(
      cardRRect,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = selected ? 2.6 : 1.8
        ..color = selected
            ? const Color(0xFFF8FAFC)
            : accentColor.withValues(alpha: 0.85),
    );
    canvas.drawLine(
      Offset(cardRect.left + 18, cardRect.top + 1.5),
      Offset(cardRect.right - 18, cardRect.top + 1.5),
      ui.Paint()
        ..strokeWidth = 1.1
        ..strokeCap = ui.StrokeCap.round
        ..color = const Color(0x38FFFFFF),
    );

    // 3. Recessed Icon Medallion + Status Ring + Optional 0–100% Circular Progress Ring
    final medallionRadius = radius * 0.56;
    canvas.drawCircle(
      center,
      medallionRadius,
      ui.Paint()..color = const Color(0xFF0A1018),
    );
    final statusColor = resolveGraphStatusRingColor(
      nodeStatus,
      accentColor.withValues(alpha: 0.55),
    );
    canvas.drawCircle(
      center,
      medallionRadius + 1.5,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 1.8
        ..color = statusColor,
    );

    if (progress case final pct?) {
      final arcRadius = radius * 0.70;
      final arcRect = Rect.fromCircle(center: center, radius: arcRadius);
      canvas.drawCircle(
        center,
        arcRadius,
        ui.Paint()
          ..style = ui.PaintingStyle.stroke
          ..strokeWidth = 3.8
          ..color = accentColor.withValues(alpha: 0.20),
      );
      if (pct > 0.001) {
        final arcColor = pct >= 0.999 ? const Color(0xFF34D399) : accentColor;
        canvas.drawArc(
          arcRect,
          -math.pi / 2,
          2 * math.pi * pct.clamp(0.0, 1.0),
          false,
          ui.Paint()
            ..style = ui.PaintingStyle.stroke
            ..strokeWidth = 4.2
            ..strokeCap = ui.StrokeCap.round
            ..color = arcColor,
        );
      }
    }

    // 4. Crisp Icon Image or Glyph
    if (iconImage case final image?) {
      final extent = radius * 1.3;
      canvas.drawImageRect(
        image,
        Rect.fromLTWH(0, 0, image.width.toDouble(), image.height.toDouble()),
        Rect.fromCenter(center: center, width: extent, height: extent),
        ui.Paint(),
      );
    } else if (icon.isNotEmpty) {
      final data = shellIcon(icon);
      final glyph = TextPainter(
        text: TextSpan(
          text: data == null
              ? icon[0].toUpperCase()
              : String.fromCharCode(data.codePoint),
          style: TextStyle(
            fontSize: radius * 0.78,
            color: const Color(0xFFEFF6FF),
            fontFamily: data?.fontFamily,
            package: data?.fontPackage,
          ),
        ),
        textDirection: TextDirection.ltr,
      )..layout();
      glyph.paint(canvas, center - Offset(glyph.width / 2, glyph.height / 2));
    }

    // 5. Optional Percentage Pill on Card Bottom
    if (_progressText case final pctPainter?) {
      final pillW = pctPainter.width + 10;
      final pillH = pctPainter.height + 3;
      final pillRect = Rect.fromCenter(
        center: Offset(center.dx, cardRect.bottom - pillH / 2 - 3),
        width: pillW,
        height: pillH,
      );
      canvas.drawRRect(
        ui.RRect.fromRectAndRadius(pillRect, const ui.Radius.circular(8)),
        ui.Paint()..color = const Color(0xEE0B121C),
      );
      canvas.drawRRect(
        ui.RRect.fromRectAndRadius(pillRect, const ui.Radius.circular(8)),
        ui.Paint()
          ..style = ui.PaintingStyle.stroke
          ..strokeWidth = 1.0
          ..color = accentColor.withValues(alpha: 0.75),
      );
      pctPainter.paint(
        canvas,
        Offset(
          center.dx - pctPainter.width / 2,
          pillRect.center.dy - pctPainter.height / 2,
        ),
      );
    }

    // 6. Title & Subtitle Labels Above Card
    var top = -6.0;
    for (final text in [_descriptionText, _titleText]) {
      if (text == null || (text.text?.toPlainText().isEmpty ?? true)) continue;
      top -= text.height;
      text.paint(canvas, Offset(center.dx - text.width / 2, top));
    }

    // 7. Attention / Error Badge
    if (badge) {
      final badgeCenter = center.translate(radius * 0.72, -radius * 0.72);
      canvas.drawCircle(
        badgeCenter,
        7,
        ui.Paint()..color = const Color(0xFFE5484D),
      );
    }

    canvas.restore();
  }
}

/// A neuron's sphere: 3D-elevated icon card with status & progress rings, driven by the behavior tree
/// its node carries (or [defaultIdleTree]). The game rewrites [blackboard] from `blackboard` on every apply.
final class SphereComponent extends NodeComponent
    with HasBehaviorTree<BaseNode> {
  SphereComponent(super.node);

  final _board = SphereBlackboard();

  @override
  SphereBlackboard get blackboard => _board;
  final body = SphereVisual();
  String _treeKey = 'unset';
  SphereIconLoader? _loadIcon;
  int _iconGeneration = 0;
  String _iconKey = '';
  bool _removed = false;
  Future<void> _iconRead = Future.value();
  Future<void> get iconReady => _iconRead;
  SphereIconLoader? get loadIcon => _loadIcon;
  set loadIcon(SphereIconLoader? value) {
    if (value == _loadIcon) return;
    _loadIcon = value;
    _iconKey = '';
    _readIcon();
  }

  double? get progress => body.progress;
  String get nodeStatus => body.nodeStatus;

  void _readIcon() {
    final icon = params['icon'] ?? '', neuron = params['neuron'] ?? nodeId;
    final key = '$neuron:$icon';
    if (key == _iconKey) return;
    _iconKey = key;
    final generation = ++_iconGeneration;
    body.iconImage?.dispose();
    body.iconImage = null;
    final load = _loadIcon;
    if (load == null || icon.isEmpty || shellIcon(icon) != null || _removed) {
      return;
    }
    _iconRead = () async {
      ui.Image? image;
      try {
        final bytes = await load(neuron, icon);
        if (bytes == null ||
            bytes.isEmpty ||
            generation != _iconGeneration ||
            _removed) {
          return;
        }
        if (utf8
            .decode(bytes.take(256).toList(), allowMalformed: true)
            .trimLeft()
            .startsWith('<')) {
          final picture = await svg.vg.loadPicture(
            svg.SvgBytesLoader(bytes),
            null,
          );
          try {
            final recorder = ui.PictureRecorder();
            ui.Canvas(recorder)
              ..scale(96 / picture.size.width, 96 / picture.size.height)
              ..drawPicture(picture.picture);
            final scaled = recorder.endRecording();
            try {
              image = await scaled.toImage(96, 96);
            } finally {
              scaled.dispose();
            }
          } finally {
            picture.picture.dispose();
          }
        } else {
          final codec = await ui.instantiateImageCodec(bytes);
          try {
            image = (await codec.getNextFrame()).image;
          } finally {
            codec.dispose();
          }
        }
        if (generation != _iconGeneration || _removed) {
          image.dispose();
          return;
        }
        body.iconImage = image;
        if (isMounted) game.wake();
      } catch (_) {
        image?.dispose();
      }
    }();
    unawaited(_iconRead);
  }

  @override
  void onRemove() {
    _removed = true;
    _iconGeneration++;
    body.iconImage?.dispose();
    body.iconImage = null;
    super.onRemove();
  }

  bool get usesDefaultTree => treeDef == null;
  bool get attentionBadge => blackboard['attention'] == 'true';

  bool get animations => body.animations;
  set animations(bool value) => body.animations = value;

  /// Whether the scene must keep drawing frames for this sphere: a tree of its own, a flag that makes the
  /// default tree move, or an effect still running.
  bool get needsFrames =>
      animations &&
      (treeDef != null ||
          blackboard['running'] == 'true' ||
          attentionBadge ||
          body.hasEffects);

  @override
  Future<void> onLoad() async {
    await super.onLoad();
    await add(body);
  }

  @override
  void changed() {
    size.setAll(96);
    super.changed();
    _readBoard();
    final resolvedProgress = parseGraphNodeProgress(
      progressParam: params['progress'] ?? blackboard['progress'],
      status: params['nodeStatus'],
      subtitle: params['description'],
      label: params['title'],
    );
    body
      ..size.setFrom(size)
      ..position.setValues(size.x / 2, size.y / 2)
      ..accentColor = sceneColor(style) ?? const Color(0xFF7FB069)
      ..icon = params['icon'] ?? ''
      ..nodeStatus = params['nodeStatus'] ?? ''
      ..selected = params['selected'] == 'true'
      ..presentationOpacity = params['state'] == 'unready' ? .45 : 1
      ..badge =
          attentionBadge ||
          params['state'] == 'error' ||
          params['nodeStatus'] == 'error' ||
          params['nodeStatus'] == 'failed'
      ..setLabels(params['title'] ?? '', params['description'] ?? '')
      ..setProgress(resolvedProgress);
    _readIcon();
    final key = treeDef == null ? '' : jsonEncode(treeDef!.toJson());
    if (key != _treeKey) {
      _treeKey = key;
      treeRoot = buildTree(treeDef ?? defaultIdleTree(), blackboard, body);
    }
    if (isMounted) game.wake();
  }

  void _readBoard() {
    blackboard.clear();
    final raw = params['blackboard'];
    if (raw == null) return;
    try {
      final decoded = jsonDecode(raw);
      if (decoded is Map) {
        for (final entry in decoded.entries) {
          blackboard['${entry.key}'] = '${entry.value}';
        }
      }
    } on FormatException {
      // The server validates the blackboard as JSON; an old shell reading a newer shape shows no facts.
    }
  }

  @override
  void update(double dt) {
    body.advance(dt);
    super.update(dt);
    if (needsFrames) game.wake();
  }
}
