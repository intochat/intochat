import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:gpt_markdown/gpt_markdown.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:url_launcher/url_launcher.dart';

import '../chat/chat_run_link.dart';
import '../chat/ui_chat_transcript.dart';
import '../chat/ui_composer.dart';
import '../components/browser/ui_web_browser.dart';
import '../components/kit/ui_kit_button.dart';
import '../components/workspace/ui_workspace_components.dart';
import '../focus_registry.dart';
import '../models/window_state.dart';
import '../nodes/brain_scene.dart';
import '../nodes/node_context.dart';
import '../nodes/node_renderer.dart';

/// Hosts one node of the brain: a published view (`view`, `confirmation`), a
/// window onto another app's view (`viewport`), or a hosted surface (`chat`,
/// `composer`, `webbrowser`). It reads the node once on mount and once per
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
  });
  final String kind, name;
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
    ChatRunLinks.follows.addListener(_followsChanged);
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
      // A refusal (a stale generation, a changed view) shows and the view is read again.
      unawaited(_reload());
      if (mounted) {
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
  );

  Widget _markdown(String text) => SelectionArea(
    child: GptMarkdown(
      text,
      key: ValueKey(text),
      onLinkTap: (url, _) async {
        final link = Uri.tryParse(url);
        final uri = link == null ? null : Uri.base.resolveUri(link);
        if (uri != null && const {'https', 'http'}.contains(uri.scheme)) {
          await launchUrl(uri);
        }
      },
    ),
  );

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
    final enabled = widget.enabled && widget.onAction != null;
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
          generation: (data['generation'] as num?)?.toInt() ?? 0,
          send: widget.onAction == null ? null : _dispatch,
          load: widget.load,
          watch: widget.watch,
          onActivate: widget.onActivate,
          knowsApp: widget.knowsApp,
          reference: (kind, name) => NodeContracts.tryResolve(kind) == null
              ? null
              : SizedBox(
                  height: 480,
                  child: _child({'kind': kind, 'name': name}),
                ),
          confirmation: widget.kind == 'confirmation',
          enabled: enabled,
        );
        final rendered = SingleChildScrollView(
          primary: false,
          child: NodeRenderer.view(
            AppView.fromJson(Map<String, dynamic>.from(view)),
            ctx,
          ),
        );
        final signals = _brainSignals;
        return signals == null
            ? rendered
            : BrainSignals(signals: signals, child: rendered);
      case 'viewport':
        final root = data['root'];
        if (root is Map && root['kind'] is String && root['name'] is String) {
          return UiMotion(
            contentKey: '${root['kind']}:${root['name']}',
            child: _child(root),
          );
        }
        return Center(child: Text('${data['error'] ?? ''}'));
      case 'composer':
        return UiComposer(
          key: ValueKey(
            '${widget.name}:${data['context']}:${data['generation']}',
          ),
          state: data,
          enabled: enabled,
          dispatch: (event) =>
              _dispatch({...event, 'kind': 'composer', 'name': widget.name}),
        );
      case 'chat':
        return _chat(data, enabled);
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

  Widget _chat(Map<String, dynamic> data, bool enabled) {
    final projection = data['projection'] as Map? ?? const {};
    final unreadIds = (data['unreadMessageIds'] as List? ?? const [])
        .whereType<String>()
        .toList();
    final transcript = UiChatTranscript(
      key: ValueKey('${projection['source']}:${projection['generation']}'),
      messages: [
        for (final message
            in (projection['messages'] as List? ?? const []).whereType<Map>())
          Map<String, dynamic>.from(message),
      ],
      markdown: _markdown,
      onRetry: enabled
          ? (text) => unawaited(
              ChatRunLinks.resend(widget.name)?.call(text) ??
                  Future<void>.value(),
            )
          : null,
      onOpenApp: widget.enabled ? widget.onActivate : null,
    );
    void act(Map action) => unawaited(
      _dispatch({
        'kind': 'chat',
        'name': widget.name,
        'action': action['id'],
        'context': projection['source'],
        'revision': projection['generation'],
      }).catchError((Object _) => null),
    );
    bool usable(Map action) => enabled && action['enabled'] != false;
    var actions = (projection['actions'] as List? ?? const [])
        .whereType<Map>()
        .toList();
    // A following composer shows Stop in place of Send; the chat keeps only its other actions.
    final stop = actions
        .where((action) => '${action['id']}'.startsWith('stop/'))
        .firstOrNull;
    final follower = ChatRunLinks.followed(widget.name);
    _publishStop(
      follower && stop != null && usable(stop)
          ? ('${stop['id']}', () => act(stop))
          : null,
    );
    if (follower) {
      actions = [
        for (final action in actions)
          if (!identical(action, stop)) action,
      ];
    }
    return Column(
      children: [
        if (unreadIds.isNotEmpty)
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12),
            child: Wrap(
              spacing: 8,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                Text('${unreadIds.length} unread'),
                TextButton(
                  onPressed: enabled
                      ? () => unawaited(
                          _dispatch({
                            'kind': 'chat',
                            'name': widget.name,
                            'context': projection['source'],
                            'revision': projection['generation'],
                            'read': {
                              'version': projection['version'],
                              'messageIds': unreadIds,
                            },
                          }).catchError((Object _) => null),
                        )
                      : null,
                  child: const Text('Mark read'),
                ),
              ],
            ),
          ),
        Expanded(child: transcript),
        if (actions.isNotEmpty)
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
            child: Wrap(
              spacing: 8,
              runSpacing: 4,
              children: [
                for (final action in actions)
                  UiKitButton(
                    label: '${action['label'] ?? ''}',
                    icon: '${action['id']}'.startsWith('stop/')
                        ? 'square'
                        : null,
                    compact: true,
                    style: UiKitButtonStyle.secondary,
                    onPressed: usable(action) ? () => act(action) : null,
                  ),
              ],
            ),
          ),
      ],
    );
  }

  // The Stop this chat offers its following composer, keyed by action id so
  // a rebuild does not republish it.
  String? _stopId;
  void _publishStop((String, VoidCallback)? stop) {
    if (stop?.$1 == _stopId) return;
    _stopId = stop?.$1;
    final name = widget.name;
    WidgetsBinding.instance.addPostFrameCallback(
      (_) => ChatRunLinks.publishStop(name, stop?.$2),
    );
  }

  void _followsChanged() {
    if (mounted && widget.kind == 'chat') setState(() {});
  }

  @override
  void dispose() {
    FocusRegistry.instance.unregister(_focusTarget, _focusKey);
    ChatRunLinks.follows.removeListener(_followsChanged);
    if (_stopId != null) {
      final name = widget.name;
      WidgetsBinding.instance.addPostFrameCallback(
        (_) => ChatRunLinks.publishStop(name, null),
      );
    }
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
