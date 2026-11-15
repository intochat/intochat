import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';

class _StepTabIntent extends Intent {
  const _StepTabIntent(this.delta);
  final int delta;
}

/// Figma `.tabs`: a 4 px gap over a 1 px `hairline` bottom border. Each tab
/// has 9x10 padding, 12 px `muted` text and a 2 px transparent bottom
/// border; the active one is `strong`, 700, with an `accent` border. The row
/// scrolls horizontally when it overflows and keeps the active tab in view.
/// Supports `←` / `→` roving keyboard navigation (`UK-14`).
/// Indexes in [disabled] are dimmed and ignore taps.
final class UiKitTabs extends StatefulWidget {
  const UiKitTabs({
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
  State<UiKitTabs> createState() => _UiKitTabsState();
}

class _UiKitTabsState extends State<UiKitTabs> {
  List<GlobalKey> _keys = [];
  final List<FocusNode> _focusNodes = [];
  int? _focusedIndex;

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
  void didUpdateWidget(UiKitTabs oldWidget) {
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

  // After layout, scroll the row just enough to show the active tab.
  void _reveal(Duration duration) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || index < 0 || index >= _keys.length) return;
      final tab = _keys[index].currentContext;
      if (tab == null) return;
      // Centred, clamped at the row's ends.
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
    return DecoratedBox(
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: c.hairline)),
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
    return Semantics(
      key: _keys[i],
      button: true,
      selected: active,
      enabled: !off,
      child: FocusableActionDetector(
        focusNode: _focusNodes[i],
        enabled: !off,
        mouseCursor: off ? SystemMouseCursors.basic : SystemMouseCursors.click,
        onShowFocusHighlight: (f) {
          setState(() {
            if (f) {
              _focusedIndex = i;
            } else if (_focusedIndex == i) {
              _focusedIndex = null;
            }
          });
        },
        shortcuts: const {
          SingleActivator(LogicalKeyboardKey.arrowLeft): _StepTabIntent(-1),
          SingleActivator(LogicalKeyboardKey.arrowRight): _StepTabIntent(1),
        },
        actions: {
          ActivateIntent: CallbackAction<ActivateIntent>(
            onInvoke: (_) {
              onChanged(i);
              return null;
            },
          ),
          _StepTabIntent: CallbackAction<_StepTabIntent>(
            onInvoke: (intent) {
              _stepFrom(i, intent.delta);
              return null;
            },
          ),
        },
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: off ? null : () => onChanged(i),
          child: Opacity(
            opacity: off ? 0.45 : 1,
            child: DecoratedBox(
              key: ValueKey('ui_kit_tab_$i'),
              decoration: BoxDecoration(
                color: focused ? c.accentTint : null,
                border: Border(
                  bottom: BorderSide(
                    width: 2,
                    color: active ? c.accent : Colors.transparent,
                  ),
                ),
              ),
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  vertical: 9,
                  horizontal: 10,
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
