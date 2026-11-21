import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:naked_ui/naked_ui.dart';

import 'ino_orb_v2.dart';

/// Quick action exposed from the floating companion orb's radial/pill bar.
final class InoCompanionAction {
  const InoCompanionAction({
    required this.id,
    required this.label,
    required this.icon,
    required this.onPressed,
  });

  final String id;
  final String label;
  final IconData icon;
  final VoidCallback onPressed;
}

/// S12c — Transparent floating Ino Companion Orb (`smart_watch_widget`-style).
///
/// Renders a free-flying, draggable 3D Lottie orb ([InoOrbV2]) over a fully
/// transparent surface with optional edge-snapping, a live System 1 / System 2
/// routing badge, and an expandable quick-action dock (Voice, Workspace Tree,
/// Live Neuron Lab).
final class InoCompanionOrb extends StatefulWidget {
  const InoCompanionOrb({
    super.key,
    this.state = PresenceState.idle,
    this.orbSize = 76,
    this.audioLevel = 0.0,
    this.initialOffset = const Offset(24, 24),
    this.snapToEdge = true,
    this.statusLine,
    this.routingBadge,
    this.actions = const [],
    this.onTapOrb,
  });

  final PresenceState state;
  final double orbSize;
  final double audioLevel;
  final Offset initialOffset;
  final bool snapToEdge;
  final String? statusLine;

  /// Optional badge such as `"S1 · 0.8ms"` or `"S2 · LLM"`.
  final String? routingBadge;
  final List<InoCompanionAction> actions;
  final VoidCallback? onTapOrb;

  @override
  State<InoCompanionOrb> createState() => _InoCompanionOrbState();
}

class _InoCompanionOrbState extends State<InoCompanionOrb> {
  late Offset _offset = widget.initialOffset;
  bool _dragging = false;
  bool _expanded = false;

  void _handleDragUpdate(DragUpdateDetails details, Size bounds) {
    final maxX = (bounds.width - widget.orbSize).clamp(0.0, double.infinity);
    final maxY = (bounds.height - widget.orbSize).clamp(0.0, double.infinity);
    setState(() {
      _dragging = true;
      _offset = Offset(
        (_offset.dx + details.delta.dx).clamp(0.0, maxX),
        (_offset.dy + details.delta.dy).clamp(0.0, maxY),
      );
    });
  }

  void _handleDragEnd(Size bounds) {
    if (!widget.snapToEdge) {
      setState(() => _dragging = false);
      return;
    }
    const edgeMargin = 16.0;
    final maxX = (bounds.width - widget.orbSize - edgeMargin).clamp(
      edgeMargin,
      double.infinity,
    );
    final snappedX = (_offset.dx + widget.orbSize / 2) < bounds.width / 2
        ? edgeMargin
        : maxX;
    setState(() {
      _dragging = false;
      _offset = Offset(snappedX, _offset.dy);
    });
  }

  @override
  Widget build(BuildContext context) {
    final halo = InoOrbV2.haloColorFor(widget.state);
    final colors = UiColors.of(context);

    return LayoutBuilder(
      builder: (context, constraints) {
        final bounds = Size(
          constraints.maxWidth.isFinite ? constraints.maxWidth : 800,
          constraints.maxHeight.isFinite ? constraints.maxHeight : 600,
        );

        return Stack(
          fit: StackFit.expand,
          children: [
            AnimatedPositioned(
              duration: _dragging
                  ? Duration.zero
                  : const Duration(milliseconds: 320),
              curve: Curves.easeOutCubic,
              left: _offset.dx,
              top: _offset.dy,
              child: GestureDetector(
                onPanUpdate: (d) => _handleDragUpdate(d, bounds),
                onPanEnd: (_) => _handleDragEnd(bounds),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        InoOrbV2(
                          state: widget.state,
                          size: widget.orbSize,
                          audioLevel: widget.audioLevel,
                          semanticsId: 'ino:companion-orb',
                          onTap: () {
                            setState(() => _expanded = !_expanded);
                            widget.onTapOrb?.call();
                          },
                        ),
                        if (widget.statusLine != null ||
                            widget.routingBadge != null) ...[
                          const SizedBox(width: 8),
                          Container(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 10,
                              vertical: 6,
                            ),
                            decoration: BoxDecoration(
                              color: colors.surface.withValues(alpha: 0.90),
                              borderRadius: BorderRadius.circular(14),
                              border: Border.all(
                                color: halo.withValues(alpha: 0.45),
                              ),
                              boxShadow: [
                                BoxShadow(
                                  color: Colors.black.withValues(alpha: 0.25),
                                  blurRadius: 14,
                                  offset: const Offset(0, 6),
                                ),
                              ],
                            ),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                if (widget.routingBadge != null)
                                  Text(
                                    widget.routingBadge!,
                                    style: TextStyle(
                                      color: halo,
                                      fontSize: 10,
                                      fontWeight: FontWeight.w700,
                                      fontFamily: UiType.mono,
                                    ),
                                  ),
                                if (widget.statusLine != null)
                                  Text(
                                    widget.statusLine!,
                                    style: TextStyle(
                                      color: colors.ink,
                                      fontSize: 12,
                                      fontWeight: FontWeight.w500,
                                    ),
                                  ),
                              ],
                            ),
                          ),
                        ],
                      ],
                    ),
                    if (_expanded && widget.actions.isNotEmpty) ...[
                      const SizedBox(height: 8),
                      Semantics(
                        identifier: 'ino:companion-actions',
                        child: Container(
                          padding: const EdgeInsets.all(6),
                          decoration: BoxDecoration(
                            color: colors.surface.withValues(alpha: 0.94),
                            borderRadius: BorderRadius.circular(16),
                            border: Border.all(color: colors.hairline),
                            boxShadow: [
                              BoxShadow(
                                color: Colors.black.withValues(alpha: 0.3),
                                blurRadius: 18,
                                offset: const Offset(0, 8),
                              ),
                            ],
                          ),
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              for (final action in widget.actions)
                                Padding(
                                  padding: const EdgeInsets.symmetric(
                                    horizontal: 2,
                                  ),
                                  child: NakedButton(
                                    onPressed: action.onPressed,
                                    child: Semantics(
                                      identifier: 'ino:action:${action.id}',
                                      button: true,
                                      label: action.label,
                                      child: Container(
                                        padding: const EdgeInsets.symmetric(
                                          horizontal: 10,
                                          vertical: 6,
                                        ),
                                        decoration: BoxDecoration(
                                          color: colors.recessed,
                                          borderRadius: BorderRadius.circular(
                                            10,
                                          ),
                                        ),
                                        child: Row(
                                          mainAxisSize: MainAxisSize.min,
                                          children: [
                                            Icon(
                                              action.icon,
                                              size: 14,
                                              color: halo,
                                            ),
                                            const SizedBox(width: 6),
                                            Text(
                                              action.label,
                                              style: TextStyle(
                                                color: colors.ink,
                                                fontSize: 11,
                                                fontWeight: FontWeight.w600,
                                              ),
                                            ),
                                          ],
                                        ),
                                      ),
                                    ),
                                  ),
                                ),
                            ],
                          ),
                        ),
                      ),
                    ],
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

/// Default quick-action presets for the floating companion orb.
List<InoCompanionAction> defaultInoCompanionActions({
  required VoidCallback onVoice,
  required VoidCallback onCodebaseTree,
  required VoidCallback onLiveNeuron,
}) => [
  InoCompanionAction(
    id: 'voice',
    label: 'Voice',
    icon: LucideIcons.mic,
    onPressed: onVoice,
  ),
  InoCompanionAction(
    id: 'tree',
    label: 'Code Tree',
    icon: LucideIcons.gitBranch,
    onPressed: onCodebaseTree,
  ),
  InoCompanionAction(
    id: 'neuron',
    label: 'Live Neuron',
    icon: LucideIcons.sparkles,
    onPressed: onLiveNeuron,
  ),
];
