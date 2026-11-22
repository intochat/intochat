import 'dart:async';

import 'package:flutter/material.dart';

import '../../core/node_contracts.dart';
import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import 'ui_control_surface.dart';

/// Invokes a typed method on a neuron contract over `NeuronChannel` (gRPC).
typedef NeuronMethodCaller = Future<Map<String, dynamic>?> Function(
  String contract,
  String key,
  String method,
  List<Object?> arguments,
);

/// Shared reactive host for every `IntoChat.OS.Modules.UI` neuron widget.
///
/// Reads initial state via `Read()`, watches the neuron's change signal over
/// `NeuronWatchMux`, and passes a typed [invoke] helper to [builder] so user
/// interactions mutate the neuron directly at 60fps without re-rendering the
/// parent screen.
class NeuronWidget extends StatefulWidget {
  const NeuronWidget({
    super.key,
    required this.kind,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
    this.eventMask = UiEventMask.none,
    this.motion = UiMotionPreset.standard,
    required this.builder,
  });

  final String kind;
  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;
  final int eventMask;
  final UiMotionPreset motion;
  final Widget Function(
    BuildContext context,
    Map<String, dynamic> state,
    Future<void> Function(String method, [List<Object?> args]) invoke,
  )
  builder;

  @override
  State<NeuronWidget> createState() => _NeuronWidgetState();
}

class _NeuronWidgetState extends State<NeuronWidget> {
  Map<String, dynamic>? _state;
  String? _error;
  bool _loading = true;
  StreamSubscription<void>? _sub;
  int _epoch = 0;

  NodeContract get _contract => NodeContracts.of(widget.kind);

  @override
  void initState() {
    super.initState();
    _subscribe();
    unawaited(_read());
  }

  @override
  void didUpdateWidget(covariant NeuronWidget oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.kind != widget.kind ||
        oldWidget.neuronKey != widget.neuronKey ||
        oldWidget.watch != widget.watch) {
      _sub?.cancel();
      _subscribe();
      unawaited(_read());
    }
  }

  void _subscribe() {
    _sub = widget.watch
        ?.call(widget.kind, widget.neuronKey)
        .listen(
          (_) => unawaited(_read()),
          onError: (Object error) {
            if (!mounted) return;
            setState(() => _error = 'Watch stream error: $error');
          },
        );
  }

  Future<void> _read() async {
    final epoch = ++_epoch;
    try {
      final result = await widget.callNeuron(
        _contract.contract,
        widget.neuronKey,
        'Read',
        const [],
      );
      if (!mounted || epoch != _epoch) return;
      setState(() {
        _state = result ?? const <String, dynamic>{};
        _error = null;
        _loading = false;
      });
    } catch (e) {
      if (!mounted || epoch != _epoch) return;
      setState(() {
        _error = '$e';
        _loading = false;
      });
    }
  }

  Future<void> _invoke(String method, [List<Object?> args = const []]) async {
    try {
      final result = await widget.callNeuron(
        _contract.contract,
        widget.neuronKey,
        method,
        args,
      );
      if (!mounted) return;
      if (result != null) {
        setState(() {
          _state = result;
          _error = null;
          _loading = false;
        });
      } else {
        await _read();
      }
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    }
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    if (_loading && _state == null) {
      return const Padding(
        padding: EdgeInsets.all(12),
        child: SizedBox(
          width: 18,
          height: 18,
          child: CircularProgressIndicator(strokeWidth: 2),
        ),
      );
    }
    if (_error != null && _state == null) {
      final c = IcTheme.of(context);
      return Semantics(
        identifier: 'neuron:${widget.kind}:${widget.neuronKey}',
        container: true,
        child: Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.error.withValues(alpha: 0.4)),
          ),
          child: Row(
            children: [
              Icon(
                uiIcon('triangle-alert') ?? Icons.error_outline,
                size: 16,
                color: c.error,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  '${widget.kind} (${widget.neuronKey}): $_error',
                  style: TextStyle(fontSize: 12, color: c.error),
                ),
              ),
              TextButton(
                onPressed: () => unawaited(_read()),
                child: const Text('Retry'),
              ),
            ],
          ),
        ),
      );
    }
    final content = widget.builder(
      context,
      _state ?? const <String, dynamic>{},
      _invoke,
    );
    return Semantics(
      identifier: 'neuron:${widget.kind}:${widget.neuronKey}',
      container: true,
      child: widget.eventMask != UiEventMask.none
          ? UiControlSurface(
              eventMask: widget.eventMask,
              motion: widget.motion,
              onEmit: (method, args) => _invoke(method, args),
              builder: (_, _) => content,
            )
          : content,
    );
  }
}
