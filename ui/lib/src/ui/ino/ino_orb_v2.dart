import 'dart:math' as math;

import 'package:dotlottie_loader/dotlottie_loader.dart';
import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lottie/lottie.dart';
import 'package:naked_ui/naked_ui.dart';

/// S10 — Ino Orb v2: stateful 3D Lottie sphere (`assets/lottie/orbit.lottie`)
/// that reacts to [PresenceState] and live voice [audioLevel] (0..1) with
/// transparent background, spring scale, and ambient halo glow.
final class InoOrbV2 extends StatefulWidget {
  const InoOrbV2({
    super.key,
    this.state = PresenceState.idle,
    this.size = 84,
    this.audioLevel = 0.0,
    this.assetPath = 'assets/lottie/orbit.lottie',
    this.onTap,
    this.onLongPress,
    this.semanticsId = 'ino:orb-v2',
  });

  final PresenceState state;
  final double size;

  /// Normalized voice / mic amplitude in `0.0..1.0` for live listening/speaking pulse.
  final double audioLevel;
  final String assetPath;
  final VoidCallback? onTap;
  final VoidCallback? onLongPress;
  final String semanticsId;

  static Color haloColorFor(PresenceState state) => switch (state) {
    PresenceState.idle => const Color(0xFF34D399),
    PresenceState.listening => const Color(0xFF38BDF8),
    PresenceState.working => const Color(0xFF818CF8),
    PresenceState.searching => const Color(0xFFA78BFA),
    PresenceState.waiting => const Color(0xFF60A5FA),
    PresenceState.paused => const Color(0xFF94A3B8),
    PresenceState.attention => const Color(0xFFF59E0B),
    PresenceState.disconnected => const Color(0xFFEF4444),
  };

  static String labelFor(PresenceState state) => switch (state) {
    PresenceState.idle => 'Ready',
    PresenceState.listening => 'Listening…',
    PresenceState.working => 'Working…',
    PresenceState.searching => 'Searching…',
    PresenceState.waiting => 'Waiting for you',
    PresenceState.paused => 'Paused',
    PresenceState.attention => 'Needs attention',
    PresenceState.disconnected => 'Disconnected',
  };

  @override
  State<InoOrbV2> createState() => _InoOrbV2State();
}

class _InoOrbV2State extends State<InoOrbV2>
    with SingleTickerProviderStateMixin {
  bool _hovered = false;
  bool _pressed = false;
  late final AnimationController _pulse = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 2400),
  );

  @override
  void initState() {
    super.initState();
    _syncPulse();
  }

  @override
  void didUpdateWidget(covariant InoOrbV2 oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.state != widget.state) {
      _syncPulse();
    }
  }

  void _syncPulse() {
    final active = switch (widget.state) {
      PresenceState.listening ||
      PresenceState.working ||
      PresenceState.searching ||
      PresenceState.attention => true,
      _ => false,
    };
    _pulse.duration = switch (widget.state) {
      PresenceState.working ||
      PresenceState.searching => const Duration(milliseconds: 1100),
      PresenceState.listening => const Duration(milliseconds: 1400),
      _ => const Duration(milliseconds: 2400),
    };
    if (active) {
      if (!_pulse.isAnimating) _pulse.repeat(reverse: true);
    } else {
      _pulse.stop();
      _pulse.value = 0.25;
    }
  }

  @override
  void dispose() {
    _pulse.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final halo = InoOrbV2.haloColorFor(widget.state);
    final clampedAudio = widget.audioLevel.clamp(0.0, 1.0);
    final interactiveScale = _pressed
        ? 0.92
        : _hovered
        ? 1.06
        : 1.0;
    final totalScale = interactiveScale + (clampedAudio * 0.14);

    return Semantics(
      identifier: widget.semanticsId,
      button: widget.onTap != null,
      label: 'Ino Orb — ${InoOrbV2.labelFor(widget.state)}',
      child: NakedButton(
        onPressed: widget.onTap,
        onLongPress: widget.onLongPress,
        onHoverChange: (h) {
          if (_hovered != h) setState(() => _hovered = h);
        },
        onPressChange: (p) {
          if (_pressed != p) setState(() => _pressed = p);
        },
        child: AnimatedScale(
          scale: totalScale,
          duration: const Duration(milliseconds: 160),
          curve: Curves.easeOutBack,
          child: AnimatedBuilder(
            animation: _pulse,
            builder: (context, child) {
              final glowSpread = 4.0 + (_pulse.value * 10.0) + (clampedAudio * 12.0);
              return Container(
                width: widget.size,
                height: widget.size,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  boxShadow: [
                    BoxShadow(
                      color: halo.withValues(alpha: 0.42),
                      blurRadius: 18 + glowSpread,
                      spreadRadius: glowSpread * 0.25,
                    ),
                  ],
                ),
                child: ClipOval(child: child),
              );
            },
            child: Stack(
              fit: StackFit.expand,
              children: [
                _FallbackSphereBackdrop(color: halo, state: widget.state),
                DotLottieLoader.fromAsset(
                  widget.assetPath,
                  frameBuilder: (context, dotlottie) {
                    if (dotlottie != null && dotlottie.animations.isNotEmpty) {
                      return ColorFiltered(
                        colorFilter: ColorFilter.mode(
                          halo.withValues(alpha: 0.22),
                          BlendMode.srcATop,
                        ),
                        child: Lottie.memory(
                          dotlottie.animations.values.first,
                          fit: BoxFit.cover,
                          repeat: widget.state != PresenceState.paused &&
                              widget.state != PresenceState.disconnected,
                          animate: widget.state != PresenceState.paused &&
                              widget.state != PresenceState.disconnected,
                        ),
                      );
                    }
                    return const SizedBox.shrink();
                  },
                  errorBuilder: (_, _, _) => const SizedBox.shrink(),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

final class _FallbackSphereBackdrop extends StatelessWidget {
  const _FallbackSphereBackdrop({required this.color, required this.state});

  final Color color;
  final PresenceState state;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        gradient: RadialGradient(
          center: const Alignment(-0.3, -0.35),
          radius: 0.85,
          colors: [
            Colors.white.withValues(alpha: 0.85),
            color.withValues(alpha: 0.88),
            color.withValues(alpha: 0.35),
            const Color(0xFF090D16).withValues(alpha: 0.92),
          ],
          stops: const [0.0, 0.28, 0.68, 1.0],
        ),
      ),
    );
  }
}

/// Transparent-background flying companion overlay (`smart_watch_widget` style)
/// that smoothly flies [InoOrbV2] to any target [position] with spring physics,
/// supports user drag-to-move, and optionally displays a compact floating HUD
/// pill beside the orb.
final class InoFlyingOrbOverlay extends StatefulWidget {
  const InoFlyingOrbOverlay({
    super.key,
    required this.position,
    this.state = PresenceState.idle,
    this.orbSize = 76,
    this.audioLevel = 0.0,
    this.statusText,
    this.onTap,
    this.onPositionChanged,
  });

  final Offset position;
  final PresenceState state;
  final double orbSize;
  final double audioLevel;
  final String? statusText;
  final VoidCallback? onTap;
  final ValueChanged<Offset>? onPositionChanged;

  @override
  State<InoFlyingOrbOverlay> createState() => _InoFlyingOrbOverlayState();
}

class _InoFlyingOrbOverlayState extends State<InoFlyingOrbOverlay> {
  late Offset _pos = widget.position;
  bool _dragging = false;

  @override
  void didUpdateWidget(covariant InoFlyingOrbOverlay oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!_dragging && oldWidget.position != widget.position) {
      _pos = widget.position;
    }
  }

  @override
  Widget build(BuildContext context) {
    final halo = InoOrbV2.haloColorFor(widget.state);
    final label = widget.statusText ?? InoOrbV2.labelFor(widget.state);

    return LayoutBuilder(
      builder: (context, constraints) {
        final maxX = math.max(0.0, constraints.maxWidth - widget.orbSize);
        final maxY = math.max(0.0, constraints.maxHeight - widget.orbSize);
        final clampedX = _pos.dx.clamp(0.0, maxX);
        final clampedY = _pos.dy.clamp(0.0, maxY);

        return Stack(
          clipBehavior: Clip.none,
          children: [
            AnimatedPositioned(
              duration: _dragging
                  ? Duration.zero
                  : const Duration(milliseconds: 520),
              curve: Curves.easeOutBack,
              left: clampedX,
              top: clampedY,
              child: GestureDetector(
                onPanStart: (_) => setState(() => _dragging = true),
                onPanUpdate: (details) {
                  setState(() {
                    _pos = Offset(
                      (_pos.dx + details.delta.dx).clamp(0.0, maxX),
                      (_pos.dy + details.delta.dy).clamp(0.0, maxY),
                    );
                  });
                },
                onPanEnd: (_) {
                  setState(() => _dragging = false);
                  widget.onPositionChanged?.call(_pos);
                },
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    InoOrbV2(
                      state: widget.state,
                      size: widget.orbSize,
                      audioLevel: widget.audioLevel,
                      onTap: widget.onTap,
                    ),
                    const SizedBox(height: 8),
                    Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 10,
                        vertical: 4,
                      ),
                      decoration: BoxDecoration(
                        color: const Color(0xCC0F172A),
                        borderRadius: BorderRadius.circular(999),
                        border: Border.all(
                          color: halo.withValues(alpha: 0.55),
                        ),
                      ),
                      child: Text(
                        label,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 11.5,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ],
        );
      },
    );
  }
}
