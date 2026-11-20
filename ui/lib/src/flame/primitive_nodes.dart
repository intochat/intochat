import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flame/components.dart';
import 'package:flame/flame.dart';
import 'package:flame/particles.dart';
import 'package:flutter/painting.dart';

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
final class PathNode extends NodeComponent {
  PathNode(super.node);
  ui.Path _path = ui.Path();

  @override
  void changed() {
    final raw = parse(params['d'] ?? '');
    final bounds = raw.getBounds();
    if (bounds.left < 0 || bounds.top < 0) {
      final dx = bounds.left < 0 ? -bounds.left : 0.0;
      final dy = bounds.top < 0 ? -bounds.top : 0.0;
      _path = raw.shift(Offset(dx, dy));
      size.setValues(bounds.right + dx, bounds.bottom + dy);
    } else {
      _path = raw;
      size.setValues(bounds.right, bounds.bottom);
    }
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
