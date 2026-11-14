import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/browser/ui_web_browser.dart';
import '../components/workspace/ui_workspace_components.dart';
import '../focus_registry.dart';
import '../models/window_state.dart';
import '../nodes/brain_scene.dart';
import '../nodes/node_context.dart';
import '../nodes/node_renderer.dart';

/// Hosts one node of the brain: a published view (`view`, `confirmation`), a
/// window onto another app's view (`viewport`), or the hosted web browser
/// (`webbrowser`). A chat transcript and its composer are view nodes. It reads the node once on mount and once per
/// change signal (`ViewChanged` for a view) and hands a view to [NodeRenderer].
class ViewHost extends StatefulWidget {
  const ViewHost({
    super.key,
    required this.kind,
    required this.name,
    required this.load,
    this.onActivate,
    this.onAction,
    required this.knowsApp,
    this.ancestors = const {},
    this.revision = 0,
    this.enabled = true,
    this.events,
    this.refreshEvery = const Duration(seconds: 2),
    this.watch,
    this.signals,
    this.embedded = false,
  });
  final String kind, name;

  /// True for a host reached through another (a viewport's root, a view's `neuronRef`): it never answers a confirmation,
  /// whatever its kind, so an approval card inside an app or unit view stays display-only.
  final bool embedded;
  final NeuronLoader load;
  final ValueChanged<Map<String, dynamic>>? onActivate;
  final NeuronAction? onAction;

  /// The app keys the shell can open; an `OpenWindow` intent for any other is refused. Pass `(_) => false` where
  /// no window may open.
  final bool Function(String app) knowsApp;
  final Set<String> ancestors;
  final int revision;
  final bool enabled;
  final NeuronEventQueue? events;
  final Duration refreshEvery;

  /// The node's change events. With a watch the host never polls: it reads once on mount and
  /// once per event. Without one it falls back to [refreshEvery].
  final Stream<void> Function(String kind, String name)? watch;

  /// The brain's journal tail from its head: given, the view's brain scenes draw it as pulses ([BrainSignals]). Only the
  /// Creator's window passes one.
  final Stream<List<JournalTailSignal>> Function()? signals;

  @override
  State<ViewHost> createState() => _ViewHostState();
}

class _ViewHostState extends State<ViewHost> {
  Map<String, dynamic>? _data;
  Object? _error;
  Timer? _timer, _resubscribe;
  StreamSubscription<void>? _watch;
  bool _loading = false, _reloadAgain = false;
  int _generation = 0;
  late final NeuronEventQueue _events = widget.events ?? NeuronEventQueue();
  final GlobalKey _focusKey = GlobalKey();
  late String _focusTarget = '${widget.kind}:${widget.name}';
  Stream<List<JournalTailSignal>>? _signals;

  // One tail per host, opened on first use and shared by the view's scenes.
  Stream<List<JournalTailSignal>>? get _brainSignals {
    final open = widget.signals;
    if (open == null) return null;
    return _signals ??= open().asBroadcastStream(
      onCancel: (subscription) => subscription.cancel(),
    );
  }

  @override
  void initState() {
    super.initState();
    FocusRegistry.instance.register(_focusTarget, _focusKey);
    _events.addListener(_reload);
    _reload();
    final watch = widget.watch;
    if (watch == null) {
      _timer = Timer.periodic(widget.refreshEvery, (_) => _reload());
    } else {
      _subscribe(watch);
    }
  }

  void _subscribe(Stream<void> Function(String, String) watch) {
    _watch?.cancel();
    _watch = watch(widget.kind, widget.name).listen(
      (_) => _reload(),
      onError: (Object _) => _reopen(watch),
      onDone: () => _reopen(watch),
    );
  }

  // A stream that ends or breaks (a proxy idle-close, a silo roll) is reopened after a pause;
  // the pause reads the node once so nothing published meanwhile is missed.
  void _reopen(Stream<void> Function(String, String) watch) {
    if (!mounted || _resubscribe != null) return;
    _resubscribe = Timer(widget.refreshEvery, () {
      _resubscribe = null;
      if (!mounted) return;
      _reload();
      _subscribe(watch);
    });
  }

  @override
  void didUpdateWidget(ViewHost old) {
    super.didUpdateWidget(old);
    final moved = old.name != widget.name || old.kind != widget.kind;
    if (moved) {
      _generation++;
      _data = null;
      _error = null;
      FocusRegistry.instance.unregister(_focusTarget, _focusKey);
      _focusTarget = '${widget.kind}:${widget.name}';
      FocusRegistry.instance.register(_focusTarget, _focusKey);
    }
    if (moved || old.revision != widget.revision) _reload();
    if (moved && widget.watch != null) _subscribe(widget.watch!);
  }

  Future<void> _reload() async {
    if (_events.pending > 0) return;
    if (_loading) {
      _reloadAgain = true;
      return;
    }
    _loading = true;
    final generation = _generation;
    final version = _events.version;
    try {
      final data = await widget.load(widget.kind, widget.name);
      if (mounted && generation == _generation && version == _events.version) {
        setState(() {
          _data = data;
          _error = null;
        });
      }
    } catch (error) {
      if (mounted &&
          generation == _generation &&
          version == _events.version &&
          (_data == null ||
              (error is UiRequestException &&
                  (error.statusCode == 401 || error.statusCode == 403)))) {
        setState(() {
          _data = null;
          _error = error;
        });
      }
    } finally {
      _loading = false;
      if (mounted && _reloadAgain) {
        _reloadAgain = false;
        unawaited(_reload());
      }
    }
  }

  Future<Object?> _dispatch(Map<String, dynamic> event) async {
    final send = widget.onAction;
    if (!widget.enabled || send == null) return null;
    try {
      return await _events.dispatch(send, event);
    } catch (error) {
      // A refusal (a stale generation, a changed view) reads the view again;
      // a 409 marked stale is retried silently by the caller without a snackbar.
      final stale =
          error is UiRequestException &&
          error.statusCode == 409 &&
          error.status == 'stale';
      unawaited(_reload());
      if (mounted && !stale) {
        ScaffoldMessenger.maybeOf(context)?.showSnackBar(
          SnackBar(content: Text('Could not save change: $error')),
        );
      }
      rethrow;
    }
  }

  Widget _child(Map child, {Key? key}) => ViewHost(
    key: key ?? ValueKey('${child['kind']}:${child['name']}'),
    kind: child['kind'] as String,
    name: child['name'] as String,
    load: widget.load,
    onActivate: widget.onActivate,
    onAction: widget.onAction,
    knowsApp: widget.knowsApp,
    events: _events,
    refreshEvery: widget.refreshEvery,
    watch: widget.watch,
    ancestors: {...widget.ancestors, '${widget.kind}:${widget.name}'},
    revision: widget.revision,
    enabled: widget.enabled,
    embedded: true,
  );

  /// The surfaces a view may host in place through a `neuronRef`: another view's window and the web browser. Never the
  /// confirmation inbox, nor a bare view.
  static const _referable = {'viewport', 'webbrowser'};

  @override
  Widget build(BuildContext context) =>
      KeyedSubtree(key: _focusKey, child: _content(context));

  Widget _content(BuildContext context) {
    final identity = '${widget.kind}:${widget.name}';
    if (widget.ancestors.contains(identity) || widget.ancestors.length >= 16) {
      return const Center(child: Text('This component cannot be displayed.'));
    }
    final data = _data;
    if (data == null) {
      return _ReadState(
        state: WindowState(
          _error == null ? WindowStatus.loading : WindowStatus.failed,
        ),
        onRetry: _error == null ? null : _reload,
      );
    }
    final enabled =
        widget.enabled &&
        widget.onAction != null &&
        !(widget.kind == 'confirmation' && widget.embedded);
    switch (widget.kind) {
      case 'view':
      case 'confirmation':
        // An app view reads as {generation, view}; the confirmation inbox as the view itself.
        final view = data['view'] is Map ? data['view'] as Map : data;
        if (view['root'] is! Map) {
          return Center(
            child: Semantics(
              liveRegion: true,
              child: const Text('Preparing this view'),
            ),
          );
        }
        final ctx = NodeContext(
          view: widget.name,
          generation: data['generation'] is num
              ? (data['generation'] as num).toInt()
              : 0,
          send: widget.onAction == null ? null : _dispatch,
          load: widget.load,
          reload: _reload,
          watch: widget.watch,
          onActivate: widget.onActivate,
          knowsApp: widget.knowsApp,
          reference: (kind, name) =>
              !_referable.contains(kind) ||
                  NodeContracts.tryResolve(kind) == null
              ? null
              : SizedBox(
                  height: 480,
                  child: _child({'kind': kind, 'name': name}),
                ),
          confirmation: widget.kind == 'confirmation' && !widget.embedded,
          enabled: enabled,
        );
        final appView = AppView.fromJson(Map<String, dynamic>.from(view));
        final fills = NodeRenderer.fills(appView.root);
        final renderedView = NodeRenderer.view(appView, ctx);
        final rendered = fills
            ? LayoutBuilder(
                builder: (context, constraints) => constraints.hasBoundedHeight
                    ? SizedBox.expand(child: renderedView)
                    : SizedBox(height: 560, child: renderedView),
              )
            : SingleChildScrollView(primary: false, child: renderedView);
        final signals = _brainSignals;
        return signals == null
            ? rendered
            : BrainSignals(signals: signals, child: rendered);
      case 'viewport':
        final root = data['root'];
        if (root is Map && root['kind'] is String && root['name'] is String) {
          final motion = UiMotion(
            contentKey: '${root['kind']}:${root['name']}',
            child: _child(root),
          );
          final error = data['error'];
          if (error is String && error.isNotEmpty) {
            return Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Padding(
                  padding: const EdgeInsets.all(8),
                  child: Text(
                    error,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ),
                Expanded(child: motion),
              ],
            );
          }
          return motion;
        }
        return Center(child: Text('${data['error'] ?? ''}'));
      case 'webbrowser':
        final send = widget.onAction;
        if (send == null) {
          return Center(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                '${data['title'] ?? ''}',
                textAlign: TextAlign.center,
                style: const TextStyle(fontWeight: FontWeight.w600),
              ),
            ),
          );
        }
        final definition = Map<String, dynamic>.from(
          data['definition'] as Map? ?? data,
        );
        return UiWebBrowser(
          part: UiWebBrowserPart.fromMetadata({
            ...definition,
            'name': widget.name,
          }),
          height: null,
          connection: '${data['connection'] ?? ''}',
          error: '${data['error'] ?? ''}',
          actualUrl: '${data['actualUrl'] ?? ''}',
          onConnect: (port, sessionId) => send({
            'kind': 'webbrowser',
            'name': widget.name,
            'action': 'connect',
            'value': jsonEncode({'port': port, 'sessionId': sessionId}),
          }),
          onDisconnect: (sessionId) => send({
            'kind': 'webbrowser',
            'name': widget.name,
            'action': 'disconnect',
            'value': jsonEncode({'sessionId': sessionId}),
          }),
          onUnavailable: (reason) => send({
            'kind': 'webbrowser',
            'name': widget.name,
            'action': 'unavailable',
            'value': reason,
          }),
        );
    }
    return const Center(child: Text('This component cannot be displayed.'));
  }

  @override
  void dispose() {
    FocusRegistry.instance.unregister(_focusTarget, _focusKey);
    _timer?.cancel();
    _resubscribe?.cancel();
    _watch?.cancel();
    _events.removeListener(_reload);
    if (widget.events == null) _events.dispose();
    super.dispose();
  }
}

class _ReadState extends StatelessWidget {
  const _ReadState({required this.state, this.onRetry});
  final WindowState state;
  final VoidCallback? onRetry;
  @override
  Widget build(BuildContext context) => state.status == WindowStatus.loading
      ? const Center(child: CircularProgressIndicator())
      : Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Text('Could not load'),
              const Text('This component could not be loaded.'),
              if (onRetry != null)
                TextButton(onPressed: onRetry, child: const Text('Retry')),
            ],
          ),
        );
}
