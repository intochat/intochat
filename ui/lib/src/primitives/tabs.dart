import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

class _StepTabIntent extends Intent {
  const _StepTabIntent(this.delta);
  final int delta;
}

/// Option B `naked_ui` (`NakedButton`) + `mix` Tabs control with roving
/// `â†` / `â†’` keyboard navigation, horizontal overflow scrolling, active tab
/// reveal, and disabled tab support.
final class UiMixTabs extends StatefulWidget {
  const UiMixTabs({
    super.key,
    required this.labels,
    required this.index,
    required this.onChanged,
    this.disabled = const {},
  });

  final List<String> labels;
  final int index;
  final ValueChanged<int> onChanged;
  final Set<int> disabled;

  @override
  State<UiMixTabs> createState() => _UiMixTabsState();
}

class _UiMixTabsState extends State<UiMixTabs> {
  List<GlobalKey> _keys = [];
  final List<FocusNode> _focusNodes = [];
  int? _focusedIndex;
  int? _hoveredIndex;

  List<String> get labels => widget.labels;
  int get index => widget.index;
  ValueChanged<int> get onChanged => widget.onChanged;
  Set<int> get disabled => widget.disabled;

  @override
  void initState() {
    super.initState();
    _syncNodes();
    _reveal(Duration.zero);
  }

  void _syncNodes() {
    while (_focusNodes.length < labels.length) {
      _focusNodes.add(FocusNode());
    }
    while (_focusNodes.length > labels.length) {
      _focusNodes.removeLast().dispose();
    }
    if (_keys.length != labels.length) {
      _keys = [for (var i = 0; i < labels.length; i++) GlobalKey()];
    }
  }

  @override
  void didUpdateWidget(UiMixTabs oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncNodes();
    if (oldWidget.index != widget.index ||
        oldWidget.labels.length != widget.labels.length) {
      final disableAnim = MediaQuery.disableAnimationsOf(context);
      _reveal(disableAnim ? Duration.zero : const Duration(milliseconds: 200));
    }
  }

  @override
  void dispose() {
    for (final node in _focusNodes) {
      node.dispose();
    }
    super.dispose();
  }

  void _stepFrom(int current, int delta) {
    if (labels.isEmpty) return;
    var candidate = current;
    for (var step = 0; step < labels.length; step++) {
      candidate = (candidate + delta + labels.length) % labels.length;
      if (!disabled.contains(candidate)) {
        _focusNodes[candidate].requestFocus();
        onChanged(candidate);
        return;
      }
    }
  }

  void _reveal(Duration duration) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || index < 0 || index >= _keys.length) return;
      final tab = _keys[index].currentContext;
      if (tab == null) return;
      Scrollable.ensureVisible(
        tab,
        alignment: 0.5,
        duration: duration,
        curve: UiTokens.of(context).ease,
      );
    });
  }

  @override
  Widget build(BuildContext context) {
    _syncNodes();
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Box(
      style: BoxStyler().border(
        BorderMix(bottom: BorderSideMix.color(c.hairline).width(1.0)),
      ),
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        primary: false,
        child: Row(
          children: [
            for (var i = 0; i < labels.length; i++) ...[
              if (i > 0) const SizedBox(width: 4),
              _tab(c, type, i),
            ],
          ],
        ),
      ),
    );
  }

  Widget _tab(UiColors c, UiTypeSet type, int i) {
    final active = i == index;
    final off = disabled.contains(i);
    final focused = _focusedIndex == i;
    final hovered = _hoveredIndex == i;

    return Semantics(
      key: _keys[i],
      button: true,
      selected: active,
      enabled: !off,
      child: Shortcuts(
        shortcuts: const {
          SingleActivator(LogicalKeyboardKey.arrowLeft): _StepTabIntent(-1),
          SingleActivator(LogicalKeyboardKey.arrowRight): _StepTabIntent(1),
        },
        child: Actions(
          actions: {
            _StepTabIntent: CallbackAction<_StepTabIntent>(
              onInvoke: (intent) {
                if (!off) _stepFrom(i, intent.delta);
                return null;
              },
            ),
          },
          child: NakedButton(
            focusNode: _focusNodes[i],
            enabled: !off,
            onPressed: off ? null : () => onChanged(i),
            onHoverChange: (h) {
              if (!mounted) return;
              setState(() {
                if (h) {
                  _hoveredIndex = i;
                } else if (_hoveredIndex == i) {
                  _hoveredIndex = null;
                }
              });
            },
            onFocusChange: (f) {
              if (!mounted) return;
              setState(() {
                if (f) {
                  _focusedIndex = i;
                } else if (_focusedIndex == i) {
                  _focusedIndex = null;
                }
              });
            },
            child: Opacity(
              opacity: off ? 0.45 : 1.0,
              child: Box(
                key: ValueKey('ui_mix_tab_$i'),
                style: BoxStyler()
                    .color(
                      focused
                          ? c.accentTint
                          : hovered && !off
                          ? c.recessed.withValues(alpha: 0.6)
                          : Colors.transparent,
                    )
                    .padding(
                      EdgeInsetsGeometryMix.symmetric(
                        vertical: 9,
                        horizontal: 10,
                      ),
                    )
                    .border(
                      BorderMix(
                        bottom: BorderSideMix.color(
                          active ? c.accent : Colors.transparent,
                        ).width(2.0),
                      ),
                    ),
                child: Text(
                  labels[i],
                  style: type.body.copyWith(
                    fontSize: 12,
                    color: active ? c.strong : c.muted,
                    fontWeight: active ? FontWeight.w700 : FontWeight.w400,
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
