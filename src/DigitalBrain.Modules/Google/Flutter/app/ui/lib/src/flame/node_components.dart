import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flame/components.dart';
import 'package:flame/effects.dart';
import 'package:flame/events.dart';
import 'package:flame/flame.dart';
import 'package:flame/particles.dart';
import 'package:flutter/animation.dart';
import 'package:flutter/painting.dart';

import 'scene_game.dart';

/// Parses a scene style: `#rrggbb` or `#aarrggbb`.
Color? sceneColor(String? style) {
  if (style == null || !style.startsWith('#')) return null;
  final hex = style.substring(1);
  final value = int.tryParse(hex, radix: 16);
  if (value == null) return null;
  return switch (hex.length) {
    6 => Color(0xFF000000 | value),
    8 => Color(value),
    _ => null,
  };
}

/// One scene node drawn in Flame. The definition sets it; state patches layer on top.
abstract class NodeComponent extends PositionComponent
    with HasPaint, HasGameReference<SceneGame>, TapCallbacks, DragCallbacks {
  NodeComponent(SceneNode node)
    : nodeId = node.id,
      typeName = node.type,
      super(anchor: Anchor.center) {
    configure(node);
  }

  final String nodeId;
  final String typeName;
  String? style;
  Map<String, String> params = const {};
  List<String> inputs = const [];
  List<SceneTween> _tweens = const [];
  String _tweenKey = '';

  Color get color => sceneColor(style) ?? const Color(0xFFFFFFFF);

  double param(String name, double fallback) =>
      double.tryParse(params[name] ?? '') ?? fallback;

  /// Resets the component to its node definition, dropping earlier patches.
  void configure(SceneNode node) {
    style = node.style;
    params = Map.of(node.parameters);
    inputs = node.inputs;
    _transform(node.transform);
    _tweens = node.tweens;
    changed();
  }

  void patch(NodePatch patch) {
    if (patch.style != null) style = patch.style;
    if (patch.parameters != null) params = {...params, ...patch.parameters!};
    if (patch.transform != null) _transform(patch.transform!);
    if (patch.tweens != null) _tweens = patch.tweens!;
    changed();
  }

  void _transform(SceneTransform t) {
    position.setValues(t.x, t.y);
    scale.setAll(t.scale);
    angle = t.rotation;
    priority = t.z.round();
    _opacity = t.opacity;
  }

  double _opacity = 1;

  /// Recomputes size and paint after the definition or a patch changed; subclasses add to it.
  void changed() {
    paint.color = color;
    opacity = _opacity.clamp(0, 1);
    final key = _tweens
        .map(
          (t) =>
              '${t.property}|${t.from}|${t.to}|${t.milliseconds}|${t.curve}|${t.repeat}',
        )
        .join(';');
    if (key != _tweenKey) {
      _tweenKey = key;
      _runTweens();
    }
  }

  void _runTweens() {
    for (final effect in children.whereType<Effect>().toList()) {
      effect.removeFromParent();
    }
    for (final tween in _tweens) {
      final controller = EffectController(
        duration: math.max(tween.milliseconds, 1) / 1000,
        curve: _curve(tween.curve),
        infinite: tween.repeat,
        alternate: tween.repeat,
      );
      final effect = switch (tween.property) {
        'x' => (() {
          position.x = tween.from;
          return MoveEffect.to(Vector2(tween.to, position.y), controller);
        })(),
        'y' => (() {
          position.y = tween.from;
          return MoveEffect.to(Vector2(position.x, tween.to), controller);
        })(),
        'scale' => (() {
          scale.setAll(tween.from);
          return ScaleEffect.to(Vector2.all(tween.to), controller);
        })(),
        'opacity' => (() {
          opacity = tween.from.clamp(0, 1);
          return OpacityEffect.to(tween.to.clamp(0, 1), controller);
        })(),
        'rotation' => (() {
          angle = tween.from;
          return RotateEffect.to(tween.to, controller);
        })(),
        _ => null,
      };
      if (effect != null) add(effect);
    }
  }

  static Curve _curve(String name) => switch (name) {
    'linear' => Curves.linear,
    'easeIn' => Curves.easeIn,
    'easeOut' => Curves.easeOut,
    'bounce' || 'bounceOut' => Curves.bounceOut,
    'elastic' || 'elasticOut' => Curves.elasticOut,
    _ => Curves.easeInOut,
  };

  @override
  bool containsLocalPoint(Vector2 point) =>
      inputs.isNotEmpty && super.containsLocalPoint(point);

  @override
  void onTapUp(TapUpEvent event) {
    game.wake();
    if (inputs.contains('tap')) {
      game.report(this, 'tap', event.localPosition);
    } else {
      event.continuePropagation = true;
    }
  }

  @override
  void onDragStart(DragStartEvent event) {
    super.onDragStart(event);
    game.wake();
    if (!inputs.contains('drag')) event.continuePropagation = true;
  }

  @override
  void onDragEnd(DragEndEvent event) {
    super.onDragEnd(event);
    if (inputs.contains('drag')) {
      game.report(
        this,
        'drag',
        position,
        '${event.velocity.x},${event.velocity.y}',
      );
    }
  }

  @override
  void onDragUpdate(DragUpdateEvent event) {
    if (inputs.contains('drag')) position.add(event.localDelta);
  }
}

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

/// `d` is a move/line list: `M x y L x y L x y Z`.
final class PathNode extends NodeComponent {
  PathNode(super.node);
  ui.Path _path = ui.Path();

  @override
  void changed() {
    _path = parse(params['d'] ?? '');
    final bounds = _path.getBounds();
    size.setValues(bounds.right, bounds.bottom);
    super.changed();
    paint
      ..style = params['fill'] == 'true'
          ? ui.PaintingStyle.fill
          : ui.PaintingStyle.stroke
      ..strokeWidth = param('stroke', 2);
  }

  static ui.Path parse(String d) {
    final path = ui.Path();
    final tokens = d
        .replaceAllMapped(RegExp('[MLZmlz]'), (m) => ' ${m[0]} ')
        .split(RegExp(r'[\s,]+'))
        .where((t) => t.isNotEmpty)
        .toList();
    var command = 'M';
    for (var i = 0; i < tokens.length;) {
      final token = tokens[i];
      if (RegExp('^[MLZmlz]\$').hasMatch(token)) {
        command = token.toUpperCase();
        i++;
        if (command == 'Z') path.close();
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

  @override
  void render(ui.Canvas canvas) => canvas.drawPath(_path, paint);
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

/// Any node type this shell does not know: a dashed outline and the type name.
final class PlaceholderNode extends NodeComponent {
  PlaceholderNode(super.node);
  TextPainter? _label;

  @override
  void changed() {
    size.setValues(param('width', 96), param('height', 48));
    super.changed();
    _label = TextPainter(
      text: TextSpan(
        text: typeName,
        style: const TextStyle(color: Color(0xFF9AA4B2), fontSize: 11),
      ),
      textDirection: TextDirection.ltr,
    )..layout(maxWidth: size.x);
  }

  @override
  void render(ui.Canvas canvas) {
    final outline = ui.Paint()
      ..color = const Color(0xFF9AA4B2)
      ..style = ui.PaintingStyle.stroke
      ..strokeWidth = 1;
    const dash = 6.0;
    void dashed(Offset from, Offset to) {
      final length = (to - from).distance;
      final step = (to - from) / length;
      for (var d = 0.0; d < length; d += dash * 2) {
        canvas.drawLine(
          from + step * d,
          from + step * math.min(d + dash, length),
          outline,
        );
      }
    }

    final r = size.toRect();
    dashed(r.topLeft, r.topRight);
    dashed(r.topRight, r.bottomRight);
    dashed(r.bottomRight, r.bottomLeft);
    dashed(r.bottomLeft, r.topLeft);
    final label = _label;
    if (label != null) {
      label.paint(
        canvas,
        Offset((size.x - label.width) / 2, (size.y - label.height) / 2),
      );
    }
  }
}

abstract final class NodeComponents {
  /// The component for a known primitive type; null for anything else.
  static NodeComponent? build(SceneNode node) => switch (node.type) {
    'circle' => CircleNode(node),
    'rect' => RectNode(node),
    'text' => TextNode(node),
    'sprite' => SpriteNode(node),
    'path' => PathNode(node),
    'particles' => ParticlesNode(node),
    'group' => GroupNode(node),
    _ => null,
  };
}
