import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

const _warning = Color(0xFFF59E0B);

/// The approved green Ino face. This programmatic artwork is a small stand-in
/// until an authored Rive asset is audited; it has no transport or domain state.
final class InoPresence extends StatefulWidget {
  const InoPresence({
    super.key,
    this.state = PresenceState.idle,
    this.size = 92,
  });

  final PresenceState state;
  final double size;

  @override
  State<InoPresence> createState() => _PresenceState();
}

final class _PresenceState extends State<InoPresence>
    with SingleTickerProviderStateMixin {
  late final AnimationController _motion = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 2200),
  );

  bool get _active => switch (widget.state) {
    PresenceState.listening ||
    PresenceState.working ||
    PresenceState.searching => true,
    _ => false,
  };

  void _syncMotion() {
    if (_active && !MediaQuery.disableAnimationsOf(context)) {
      if (!_motion.isAnimating) _motion.repeat(reverse: true);
    } else {
      _motion.stop();
      _motion.value = 0;
    }
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncMotion();
  }

  @override
  void didUpdateWidget(InoPresence oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncMotion();
  }

  @override
  void dispose() {
    _motion.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Semantics(
    image: true,
    label:
        'Ino, ${switch (widget.state) {
          PresenceState.idle => 'ready when you are',
          PresenceState.listening => 'listening',
          PresenceState.working => 'working',
          PresenceState.searching => 'searching',
          PresenceState.waiting => 'waiting for you',
          PresenceState.paused => 'paused',
          PresenceState.attention => 'needs attention',
          PresenceState.disconnected => 'disconnected',
        }}',
    child: RepaintBoundary(
      child: SizedBox.square(
        dimension: widget.size,
        child: AnimatedBuilder(
          animation: _motion,
          builder: (context, _) => CustomPaint(
            painter: _InoPainter(state: widget.state, phase: _motion.value),
          ),
        ),
      ),
    ),
  );
}

/// Paints Ino's face into [size] at [phase] (0..1); the glass sphere draws it while listening and searching.
void paintInoFace(
  Canvas canvas,
  Size size,
  PresenceState state,
  double phase,
) => _InoPainter(state: state, phase: phase).paint(canvas, size);

final class _InoPainter extends CustomPainter {
  const _InoPainter({required this.state, required this.phase});

  final PresenceState state;
  final double phase;

  @override
  void paint(Canvas canvas, Size size) {
    final edge = math.min(size.width, size.height);
    canvas.save();
    canvas.scale(edge / 100);
    final rect = Rect.fromLTWH(7, 7 + phase * 1.4, 86, 86);
    final shape = RRect.fromRectAndRadius(rect, const Radius.circular(29));
    final path = Path()..addRRect(shape);
    canvas.drawShadow(path, const Color(0x243C6D50), 9, false);
    canvas.drawRRect(
      shape,
      Paint()
        ..shader = LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: state == PresenceState.disconnected
              ? const [Color(0xFFE2E8E1), Color(0xFFC5CEC4)]
              : const [Color(0xFFE9F4DF), Color(0xFFC2D9B7)],
        ).createShader(rect),
    );
    canvas.drawRRect(
      shape,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.1
        ..color = const Color(0xFFD0E0C7),
    );
    final face = Paint()
      ..color = state == PresenceState.attention
          ? _warning
          : const Color(0xFF405E48)
      ..strokeWidth = 3.8
      ..strokeCap = StrokeCap.round
      ..style = PaintingStyle.stroke;
    final looking = state == PresenceState.searching ? phase * 3 - 1.5 : 0.0;
    final eyeY = 43 + phase;
    for (final x in [38.0, 62.0]) {
      canvas.drawLine(
        Offset(x + looking, eyeY - 2),
        Offset(x + looking, eyeY + 3),
        face,
      );
    }
    final mouth = Path()..moveTo(43, 61 + phase);
    if (state == PresenceState.attention ||
        state == PresenceState.disconnected) {
      mouth.lineTo(57, 61 + phase);
    } else {
      mouth.quadraticBezierTo(50, 67 + phase, 57, 61 + phase);
    }
    canvas.drawPath(mouth, face..strokeWidth = 2.6);
    if (state == PresenceState.waiting) {
      canvas.drawCircle(const Offset(86, 16), 9, Paint()..color = _warning);
      canvas.drawLine(
        const Offset(86, 11),
        const Offset(86, 17),
        Paint()
          ..color = Colors.white
          ..strokeWidth = 2.2
          ..strokeCap = StrokeCap.round,
      );
      canvas.drawCircle(
        const Offset(86, 21),
        1.2,
        Paint()..color = Colors.white,
      );
    }
    canvas.restore();
  }

  @override
  bool shouldRepaint(_InoPainter oldDelegate) =>
      state != oldDelegate.state || phase != oldDelegate.phase;
}
