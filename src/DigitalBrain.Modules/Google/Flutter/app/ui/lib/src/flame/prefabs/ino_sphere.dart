import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flame/components.dart';
import 'package:flame/particles.dart';

import '../node_components.dart';

enum InoMood {
  idle,
  listening,
  thinking,
  acting,
  speaking,
  error,
  disconnected,
}

/// Ino as a Flame node: a green core, two orbiting rings and a halo whose [glow] eases toward
/// the mood's target. The Flame counterpart of `InoPresence`, in its lumen greens.
final class InoSphere extends NodeComponent {
  InoSphere(super.node);

  InoMood mood = InoMood.idle;
  double glow = 0;
  double spin = 0;
  double _time = 0;

  /// Off when the platform asks for reduced motion (`MediaQuery.disableAnimationsOf`); the
  /// sphere then jumps to its target look and stays still.
  bool animations = true;

  static InoMood parseMood(String? value) => InoMood.values.firstWhere(
    (m) => m.name == value,
    orElse: () => InoMood.idle,
  );

  double get radius => param('radius', 48).clamp(4, 2000).toDouble();

  double get _target => switch (mood) {
    InoMood.idle => 0.3,
    InoMood.listening => 0.6,
    InoMood.thinking => 0.9,
    InoMood.acting => 1.0,
    InoMood.speaking => 0.7 + 0.2 * math.sin(_time * 2 * math.pi),
    InoMood.error => 0.6,
    InoMood.disconnected => 0.1,
  };

  double get _spinSpeed => switch (mood) {
    InoMood.thinking => 3.0,
    InoMood.acting => 2.0,
    InoMood.disconnected => 0,
    _ => 0.6,
  };

  @override
  void changed() {
    size.setAll(radius * 2);
    final next = parseMood(params['mood']);
    final burst = next == InoMood.acting && mood != InoMood.acting;
    mood = next;
    super.changed();
    if (burst && animations) _burst();
    if (isMounted) game.wake();
  }

  // Still once the glow has arrived and nothing spins: disconnected, or reduced motion.
  bool get _settled =>
      (glow - _target).abs() < 0.001 &&
      mood == InoMood.disconnected &&
      children.whereType<ParticleSystemComponent>().isEmpty;

  @override
  void update(double dt) {
    super.update(dt);
    if (!animations) {
      glow = _target;
      return;
    }
    _time += dt;
    glow += (_target - glow) * (1 - math.exp(-dt * 6));
    spin += _spinSpeed * dt;
    if (!_settled) game.wake();
  }

  void _burst() {
    final random = math.Random();
    final dot = ui.Paint()..color = const ui.Color(0xFFC2D9B7);
    add(
      ParticleSystemComponent(
        position: size / 2,
        particle: Particle.generate(
          count: 16,
          lifespan: 0.8,
          generator: (_) {
            final a = random.nextDouble() * 2 * math.pi;
            return AcceleratedParticle(
              speed: Vector2(math.cos(a), math.sin(a)) * radius * 1.5,
              child: CircleParticle(radius: 2, paint: dot),
            );
          },
        ),
      ),
    );
  }

  ui.Color get _tint => switch (mood) {
    InoMood.error => const ui.Color(0xFFAD4F3F),
    InoMood.disconnected => const ui.Color(0xFFC5CEC4),
    _ => _hsv(param('hue', 105).clamp(0, 360).toDouble(), 0.35, 0.72),
  };

  static ui.Color _hsv(double h, double s, double v) {
    final c = v * s;
    final x = c * (1 - ((h / 60) % 2 - 1).abs());
    final m = v - c;
    final (r, g, b) = switch ((h / 60).floor()) {
      0 => (c, x, 0.0),
      1 => (x, c, 0.0),
      2 => (0.0, c, x),
      3 => (0.0, x, c),
      4 => (x, 0.0, c),
      _ => (c, 0.0, x),
    };
    return ui.Color.from(alpha: 1, red: r + m, green: g + m, blue: b + m);
  }

  @override
  void render(ui.Canvas canvas) {
    final c = (size / 2).toOffset();
    final r = radius;
    final tint = _tint;
    final haloRadius = r * (1.2 + 0.5 * glow);
    canvas.drawCircle(
      c,
      haloRadius,
      ui.Paint()
        ..shader = ui.Gradient.radial(c, haloRadius, [
          tint.withValues(alpha: 0.45 * glow),
          tint.withValues(alpha: 0),
        ]),
    );
    canvas.drawCircle(
      c,
      r * 0.7,
      ui.Paint()
        ..shader = ui.Gradient.radial(
          c.translate(-r * 0.3, -r * 0.3),
          r * 1.2,
          [
            mood == InoMood.disconnected
                ? const ui.Color(0xFFE2E8E1)
                : const ui.Color(0xFFE9F4DF),
            tint,
          ],
        ),
    );
    final ring = ui.Paint()
      ..style = ui.PaintingStyle.stroke
      ..strokeWidth = 1.5
      ..color = const ui.Color(0xFF405E48).withValues(alpha: 0.35 + 0.5 * glow);
    for (final (direction, tilt) in [(1.0, 0.35), (-0.7, 0.6)]) {
      canvas
        ..save()
        ..translate(c.dx, c.dy)
        ..rotate(spin * direction)
        ..drawOval(
          ui.Rect.fromCenter(
            center: ui.Offset.zero,
            width: r * 1.8,
            height: r * 1.8 * tilt,
          ),
          ring,
        )
        ..restore();
    }
  }
}
