import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';

// Ino: the assistant's face in the dock, driven by the latest AppActivity the assistant app published,
// with its Detail as the caption. A feed that ends or fails shows Ino disconnected and is reopened
// with a growing pause; its retained value makes the reopened feed current at once. With no app
// focused Ino is large and bobs; docked beside an app's toolbar it is small and still. The float
// stops under reduced motion and while the route is not ticking.
class AssistantIsland extends StatefulWidget {
  const AssistantIsland({
    super.key,
    this.watch,
    this.brainId,
    this.floating = false,
    this.retryAfter = const Duration(seconds: 1),
    this.retryCeiling = const Duration(seconds: 30),
    this.stableAfter = const Duration(seconds: 5),
  });

  final Stream<AppActivity> Function()? watch;
  // The brain the feed belongs to; a different brain reopens the feed.
  final String? brainId;
  final bool floating;
  // A feed that stays open this long counts as healthy: only then does the retry pause start over.
  final Duration retryAfter, retryCeiling, stableAfter;

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
  Timer? _retry, _stable;
  bool _bobbed = false;
  late Duration _pause = widget.retryAfter;

  static InoPresenceState? _stateOf(String name) =>
      InoPresenceState.values.where((s) => s.name == name).firstOrNull;

  @override
  void initState() {
    super.initState();
    _connect();
  }

  void _connect() {
    unawaited(_subscription?.cancel());
    _retry?.cancel();
    _stable?.cancel();
    _stable = Timer(widget.stableAfter, () => _pause = widget.retryAfter);
    _subscription = widget.watch?.call().listen(
      (activity) {
        final state = _stateOf(activity.state);
        if (activity.app != _app || state == null) return;
        setState(() {
          _state = state;
          _detail = activity.detail;
          _bobbed = false;
        });
        _syncFloat();
      },
      onError: (Object _) => _lost(),
      onDone: _lost,
      cancelOnError: true,
    );
  }

  void _lost() {
    _stable?.cancel();
    if (!mounted) return;
    if (_state != InoPresenceState.disconnected || _detail.isNotEmpty) {
      setState(() {
        _state = InoPresenceState.disconnected;
        _detail = '';
      });
    }
    _retry?.cancel();
    _retry = Timer(_pause, _connect);
    final doubled = _pause * 2;
    _pause = doubled > widget.retryCeiling ? widget.retryCeiling : doubled;
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _syncFloat();
  }

  @override
  void didUpdateWidget(AssistantIsland oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.brainId != widget.brainId) {
      _pause = widget.retryAfter;
      _state = InoPresenceState.idle;
      _detail = '';
      _connect();
    }
    _syncFloat();
  }

  // Ino bobs a few times, then rests until its state changes again: an idle shell holds no
  // forever-running animation.
  void _syncFloat() {
    if (widget.floating &&
        !MediaQuery.disableAnimationsOf(context) &&
        TickerMode.valuesOf(context).enabled) {
      if (!_float.isAnimating && !_bobbed) {
        _bobbed = true;
        _float.value = 0;
        unawaited(
          _float
              .repeat(reverse: true, count: 6)
              .whenComplete(() => _float.value = 0),
        );
      }
    } else {
      _float.stop();
      _float.value = 0;
    }
  }

  @override
  void dispose() {
    _retry?.cancel();
    _stable?.cancel();
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
                child: Tooltip(
                  message: _detail,
                  child: Text(
                    _detail,
                    key: const ValueKey('ino-caption'),
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    semanticsLabel: 'Ino: $_detail',
                    style: Theme.of(context).textTheme.bodySmall,
                  ),
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
