import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flame/components.dart';
import 'package:flutter/painting.dart';
import 'package:intochat_design_ui/intochat_design_ui.dart'
    show IcBrainShaderMode;
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import '../../lumen/neuron_icon.dart';
import '../ino_presence.dart';

const _accent = Color(0xFF3D7EFF);
const _grey = Color(0xFF9AA0A6);
const _red = Color(0xFFE5484D);
const _warm = Color(0xFFFF7A45);

/// The colour a presence state shows: the OS bar's dot uses it too.
Color presenceColor(PresenceState state) => switch (state) {
  PresenceState.attention => _warm,
  PresenceState.paused => _red,
  PresenceState.disconnected => _grey,
  PresenceState.waiting => const Color(0xFFF59E0B),
  _ => _accent,
};

/// The colour of a focus tone: info is the accent.
Color focusToneColor(FocusTone tone) => switch (tone) {
  FocusTone.info => _accent,
  FocusTone.success => const Color(0xFF10B981),
  FocusTone.warning => const Color(0xFFF59E0B),
  FocusTone.critical => _red,
};

/// Ino's sphere: a volumetric glass ball (radial specular body, rim light, blurred glow) whose mood follows
/// [state]. Inside it draws the face while listening or searching, else the glyph of [neuronKind].
/// Only [IcBrainShaderMode.volumetricGlass] has its own look; the other modes draw the same glass.
final class GlassSphere extends PositionComponent {
  GlassSphere({
    this.state = PresenceState.idle,
    this.neuronKind,
    this.mode = IcBrainShaderMode.volumetricGlass,
    double radius = 28,
  }) : super(anchor: Anchor.center, size: Vector2.all(radius * 2));

  PresenceState state;
  String? neuronKind;
  IcBrainShaderMode mode;

  /// Off under reduced motion: the mood is drawn at its rest frame.
  bool animations = true;
  double time = 0;

  double get radius => size.x / 2;

  @override
  void update(double dt) {
    if (animations) time += dt;
  }

  double get _phase => animations ? (math.sin(time * math.pi) + 1) / 2 : 0;

  @override
  void render(ui.Canvas canvas) {
    final center = (size / 2).toOffset();
    final pulse = state == PresenceState.working && animations
        ? 1 + 0.06 * math.sin(time * 2 * math.pi / 1.2)
        : 1.0;
    final r = radius * 0.78 * pulse;
    final muted =
        state == PresenceState.paused || state == PresenceState.disconnected;
    final body = muted
        ? (state == PresenceState.paused ? const Color(0xFF8A93A3) : _grey)
        : _accent;
    final rim = switch (state) {
      PresenceState.attention => _warm,
      PresenceState.paused => _red,
      _ => const Color(0xCCFFFFFF),
    };

    // Glow: the painter's blurred halo, warmer when the sphere wants attention.
    final glow =
        state == PresenceState.attention || state == PresenceState.paused
        ? rim
        : body;
    canvas.drawCircle(
      center,
      r * 1.08,
      ui.Paint()
        ..color = glow.withValues(
          alpha: state == PresenceState.attention ? 0.75 : 0.4,
        )
        ..maskFilter = const ui.MaskFilter.blur(ui.BlurStyle.normal, 10),
    );

    // Body: the radial specular shader of the volumetric glass mode.
    final rect = Rect.fromCircle(center: center, radius: r);
    canvas.drawCircle(
      center,
      r,
      ui.Paint()
        ..shader = RadialGradient(
          center: const Alignment(-0.38, -0.38),
          radius: 0.95,
          colors: [
            Color.lerp(body, const Color(0xFFFFFFFF), 0.55)!,
            body.withValues(alpha: 0.92),
            Color.lerp(body, const Color(0xFF000000), 0.55)!,
          ],
          stops: const [0.0, 0.52, 1.0],
        ).createShader(rect),
    );

    // Specular highlight and rim light.
    canvas.drawCircle(
      center.translate(-r * 0.36, -r * 0.4),
      r * 0.22,
      ui.Paint()
        ..color = const Color(0x8CFFFFFF)
        ..maskFilter = const ui.MaskFilter.blur(ui.BlurStyle.normal, 3),
    );
    canvas.drawCircle(
      center,
      r,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth =
            state == PresenceState.attention || state == PresenceState.paused
            ? 2.4
            : 1.1
        ..color = rim,
    );

    if (state == PresenceState.listening || state == PresenceState.searching) {
      final edge = r * 1.2;
      canvas.save();
      canvas.translate(center.dx - edge / 2, center.dy - edge / 2);
      paintInoFace(canvas, Size.square(edge), state, _phase);
      canvas.restore();
    } else {
      _paintGlyph(canvas, center, r);
    }

    if (state == PresenceState.waiting) {
      _paintDottedRing(canvas, center, r * 1.22);
    }
  }

  void _paintGlyph(ui.Canvas canvas, Offset center, double r) {
    final kind = neuronKind;
    final known = NeuronIconKind.fromKey(kind);
    final String text;
    String? family, package;
    if (kind == null || kind.isEmpty) {
      final data = NeuronIcon.glyphOf(NeuronIconKind.assistant);
      text = String.fromCharCode(data.codePoint);
      family = data.fontFamily;
      package = data.fontPackage;
    } else if (known == NeuronIconKind.generic) {
      text = kind[0].toUpperCase();
    } else {
      final data = NeuronIcon.glyphOf(known);
      text = String.fromCharCode(data.codePoint);
      family = data.fontFamily;
      package = data.fontPackage;
    }
    final glyph = TextPainter(
      text: TextSpan(
        text: text,
        style: TextStyle(
          fontSize: r * 0.9,
          color: const Color(0xF2FFFFFF),
          fontFamily: family,
          package: package,
        ),
      ),
      textDirection: TextDirection.ltr,
    )..layout();
    glyph.paint(canvas, center - Offset(glyph.width / 2, glyph.height / 2));
  }

  void _paintDottedRing(ui.Canvas canvas, Offset center, double ringRadius) {
    const dots = 12;
    final turn = animations ? time * 0.25 * 2 * math.pi : 0.0;
    final paint = ui.Paint()..color = const Color(0xFFF59E0B);
    for (var i = 0; i < dots; i++) {
      final angle = turn + 2 * math.pi * i / dots;
      canvas.drawCircle(
        center + Offset(math.cos(angle), math.sin(angle)) * ringRadius,
        2,
        paint,
      );
    }
  }
}

/// The presence layer's own outline around a focused rect, in the tone's colour. It draws on the layer, never
/// inside the target's view.
final class HighlightRing extends PositionComponent {
  HighlightRing({this.tone = FocusTone.info});

  FocusTone tone;
  bool visible = false;

  @override
  void render(ui.Canvas canvas) {
    if (!visible) return;
    final color = focusToneColor(tone);
    final shape = RRect.fromRectAndRadius(
      Rect.fromLTWH(0, 0, size.x, size.y),
      const Radius.circular(12),
    );
    canvas.drawRRect(
      shape,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 6
        ..color = color.withValues(alpha: 0.35)
        ..maskFilter = const ui.MaskFilter.blur(ui.BlurStyle.normal, 6),
    );
    canvas.drawRRect(
      shape,
      ui.Paint()
        ..style = ui.PaintingStyle.stroke
        ..strokeWidth = 2.5
        ..color = color,
    );
  }
}
