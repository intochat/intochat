import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';

// Ino: the assistant's face in the middle of the dock, driven by the latest AppActivity the assistant
// app published, with its Detail as the caption. A failing feed shows Ino disconnected. With no app
// focused Ino floats; the motion stops under reduced motion.
class AssistantIsland extends StatefulWidget {
  const AssistantIsland({super.key, this.watch, this.floating = false});

  final Stream<AppActivity> Function()? watch;
  final bool floating;

  @override
  State<AssistantIsland> createState() => _AssistantIslandState();
}

class _AssistantIslandState extends State<AssistantIsland>
    with SingleTickerProviderStateMixin {
  static const _app = 'assistant';
  late final AnimationController _float = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 2600),
  );
  InoPresenceState _state = InoPresenceState.idle;
  String _detail = '';
  StreamSubscription<AppActivity>? _subscription;

  static InoPresenceState _stateOf(String name) => InoPresenceState.values
      .firstWhere((s) => s.name == name, orElse: () => InoPresenceState.idle);

  @override
  void initState() {
    super.initState();
    _subscription = widget.watch?.call().listen(
      (activity) {
        if (activity.app != _app) return;
        setState(() {
          _state = _stateOf(activity.state);
          _detail = activity.detail;
        });
      },
      onError: (Object _) => setState(() {
        _state = InoPresenceState.disconnected;
        _detail = '';
      }),
    );
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncFloat();
  }

  @override
  void didUpdateWidget(AssistantIsland oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncFloat();
  }

  void _syncFloat() {
    if (widget.floating && !MediaQuery.disableAnimationsOf(context)) {
      if (!_float.isAnimating) _float.repeat(reverse: true);
    } else {
      _float.stop();
      _float.value = 0;
    }
  }

  @override
  void dispose() {
    unawaited(_subscription?.cancel());
    _float.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final ino = AnimatedBuilder(
      animation: _float,
      builder: (context, child) => Transform.translate(
        key: const ValueKey('ino-float'),
        offset: Offset(0, -6 * Curves.easeInOut.transform(_float.value)),
        child: child,
      ),
      child: InoPresence(
        key: const ValueKey('assistant-ino'),
        state: _state,
        size: widget.floating ? 64 : 36,
      ),
    );
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 6),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          ino,
          if (_detail.isNotEmpty)
            ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 180),
              child: Padding(
                padding: const EdgeInsets.only(left: 8),
                child: Text(
                  _detail,
                  key: const ValueKey('ino-caption'),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            ),
        ],
      ),
    );
  }
}

// The text kind: one line; submit posts the trimmed text and clears the field.
class DockTextField extends StatefulWidget {
  const DockTextField({super.key, required this.hint, required this.onSubmit});

  final String hint;
  final ValueChanged<String> onSubmit;

  @override
  State<DockTextField> createState() => _DockTextFieldState();
}

class _DockTextFieldState extends State<DockTextField> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _submit(String text) {
    final trimmed = text.trim();
    if (trimmed.isEmpty) return;
    widget.onSubmit(trimmed);
    _controller.clear();
  }

  @override
  Widget build(BuildContext context) => SizedBox(
    width: 360,
    child: TextField(
      controller: _controller,
      decoration: InputDecoration(
        hintText: widget.hint,
        isDense: true,
        border: const OutlineInputBorder(),
      ),
      textInputAction: TextInputAction.send,
      onSubmitted: _submit,
    ),
  );
}
