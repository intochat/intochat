import 'dart:async';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/browser/ui_web_browser.dart';
import '../components/workspace/ui_workspace_components.dart';
import '../focus_registry.dart';
import '../core/neuron_method_caller.dart';
import '../runtime/models/window_state.dart';
import '../nodes/brain_scene.dart';
import '../nodes/error_line.dart';
import '../nodes/neuron_ref.dart';
import '../nodes/node_context.dart';
import '../nodes/node_renderer.dart';
import 'brain_session_controller.dart';
import 'child_route.dart';

/// Hosts one node of the brain: a published view (`view`, `confirmation`), a
/// window onto another app's view (`viewport`), a live nested child or embed (`neuronRef`),
/// or the hosted web browser (`webbrowser`). A chat transcript and its composer
/// are view nodes. It reads the node once on mount and once per change signal
/// (`ViewChanged` for a view) and hands a view to [NodeRenderer].
class ViewHost extends StatefulWidget {
  const ViewHost({
    super.key,
    required this.kind,
    required this.name,
    this.load,
    this.onActivate,
    this.onAction,
    this.knowsApp,
    this.ancestors = const {},
    this.childDepth = 0,
    this.childAncestors = const {},
    this.actNeuron,
    this.childPath = '',
    this.childProps = const {},
    this.inlineChild = false,
    this.revision = 0,
    this.enabled = true,
    this.events,
    this.refreshEvery = const Duration(seconds: 2),
    this.watch,
    this.watchSignal,
    this.callNeuron,
    this.signals,
    this.transcribe,
    this.talk,
    this.speak,
    this.storeSecret,
    this.embedded = false,
  });
  final String kind, name;

  /// True for a host reached through another (a viewport's root, a view's `neuronRef`): it never answers a confirmation,
  /// whatever its kind, so an approval card inside an app or unit view stays display-only.
  final bool embedded;

  /// True when mounted inline via a `NeuronRef` (`Screen.Child<T>`, wire `refKind: 'child'`) inside a parent view or chat turn.
  final bool inlineChild;

  /// Current `NeuronRef` (`Screen.Child<T>`) recursion depth (capped at [maxEmbedDepth] = 4).
  final int childDepth;

  /// Visited `neuronUri` / resolved view names along the active child-ref chain.
  final Set<String> childAncestors;

  /// For a UI child (wire `refKind: 'child'`): the root neuron its acts name and its key path from it (see [NodeContext.actNeuron]).
  final String? actNeuron;
  final String childPath;

  /// For a UI child: the props its parent wrote on the `NeuronRef` node (see [NodeContext.childProps]).
  final Map<String, dynamic> childProps;

  final NeuronLoader? load;
  final ValueChanged<Map<String, dynamic>>? onActivate;
  final NeuronAction? onAction;

  /// The app keys the shell can open; an `OpenWindow` intent for any other is refused. Defaults to
  /// [BrainSessionScope] when present, or `(_) => false` when mounted standalone.
  final bool Function(String app)? knowsApp;
  final Set<String> ancestors;
  final int revision;
  final bool enabled;
  final NeuronEventQueue? events;
  final Duration refreshEvery;

  /// Test hook: receives each view context this host builds, so a widget test
  /// can act through the real host (its dispatch, refusal display and reload).
  @visibleForTesting
  static void Function(String name, NodeContext ctx)? debugOnContext;

  /// The node's change events. With a watch the host never polls: it reads once on mount and
  /// once per event. Without one it falls back to [refreshEvery].
  final Stream<void> Function(String kind, String name)? watch;

  /// Watches one signal type on a neuron contract, yielding decoded payloads.
  final NeuronSignalWatcher? watchSignal;
  final NeuronMethodCaller? callNeuron;

  /// The brain's journal tail from its head: given, the view's brain scenes draw it as pulses ([BrainSignals]). Only the
  /// Creator's window passes one.
  final Stream<List<JournalTailSignal>> Function()? signals;

  /// Host capability used by dictation composers to transcribe captured audio into a draft.
  final VoiceTranscriber? transcribe;

  /// Host capability used to open full-duplex voice mode on a conversation.
  final VoiceTalker? talk;

  /// Host capability used by read-aloud controls to synthesize assistant replies.
  final VoiceSpeaker? speak;

  /// Host capability a form uses to store a secret field in the person's vault.
  final SecretStore? storeSecret;

  @override
  State<ViewHost> createState() => _ViewHostState();
}

class _ViewHostState extends State<ViewHost> {
  Map<String, dynamic>? _data;
  Object? _error;
  Timer? _timer, _resubscribe;
  StreamSubscription<void>? _watch;
  String _watchedKind = '', _watchedName = '';
  int _watchGeneration = 0;
  bool _loading = false, _reloadAgain = false;
  int _generation = 0;
  late final NeuronEventQueue _events = widget.events ?? NeuronEventQueue();
  final GlobalKey _focusKey = GlobalKey();
  late String _focusTarget = '${widget.kind}:${widget.name}';
  Stream<List<JournalTailSignal>>? _signals;

  BrainSessionController? get _session => BrainSessionScope.readOf(context);

  NeuronLoader get _effectiveLoad {
    final load = widget.load ?? _session?.loadNode;
    assert(
      load != null,
      'ViewHost requires either an explicit load callback or an ancestor BrainSessionScope.',
    );
    return load!;
  }

  Stream<void> Function(String kind, String name)? get _effectiveWatch {
    if (widget.watch != null) return widget.watch;
    final session = _session;
    return session != null && session.hasNodeWatch ? session.watchNode : null;
  }

  NeuronSignalWatcher? get _effectiveWatchSignal =>
      widget.watchSignal ?? _session?.watchSignal;
  NeuronMethodCaller? get _effectiveCallNeuron =>
      widget.callNeuron ?? _session?.callNeuron;
  NeuronAction? get _effectiveOnAction =>
      widget.onAction ?? _session?.sendAction;
  ValueChanged<Map<String, dynamic>>? get _effectiveOnActivate =>
      widget.onActivate ?? _session?.activate;
  bool Function(String app) get _effectiveKnowsApp =>
      widget.knowsApp ?? _session?.knowsApp ?? (_) => false;
  VoiceTranscriber? get _effectiveTranscribe =>
      widget.transcribe ?? _session?.transcribe;
  VoiceTalker? get _effectiveTalk => widget.talk ?? _session?.talk;
  VoiceSpeaker? get _effectiveSpeak => widget.speak ?? _session?.speak;
  SecretStore? get _effectiveStoreSecret =>
      widget.storeSecret ?? _session?.storeSecret;

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
    final watch = _effectiveWatch;
    if (watch == null) {
      _timer = Timer.periodic(widget.refreshEvery, (_) => _reload());
    } else {
      final initial = _initialWatchTarget();
      _subscribe(watch, initial.$1, initial.$2);
    }
  }

  (String, String) _initialWatchTarget() {
    if (widget.kind == 'screen' ||
        (widget.kind == 'view' &&
            IntoChatOSUiClient.isExplicitScreenAddress(widget.name))) {
      final key =
          IntoChatOSUiClient.screenNeuronKey('', widget.name) ??
          widget.name.trim();
      return ('screen', key);
    }
    return (widget.kind, widget.name);
  }

  void _syncWatchForData(Map<String, dynamic> data) {
    final watch = _effectiveWatch;
    if (watch == null || (widget.kind != 'view' && widget.kind != 'screen')) {
      return;
    }
    final rawView = data['view'] is Map ? data['view'] as Map : data;
    final isScreen =
        rawView['acts'] == 'act' ||
        widget.kind == 'screen' ||
        IntoChatOSUiClient.isExplicitScreenAddress(widget.name);
    if (!isScreen) return;
    final alias = data['alias'] is String ? data['alias'] as String : null;
    final screenKey = IntoChatOSUiClient.screenNeuronKey(
      '',
      widget.name,
      alias: alias,
    );
    if (screenKey == null) return;
    if (_watchedKind == 'screen' && _watchedName == screenKey) return;
    _subscribe(watch, 'screen', screenKey);
  }

  void _subscribe(
    Stream<void> Function(String, String) watch,
    String kind,
    String name,
  ) {
    _resubscribe?.cancel();
    _resubscribe = null;
    _watch?.cancel();
    _watchedKind = kind;
    _watchedName = name;
    final subGeneration = ++_watchGeneration;
    _watch = watch(kind, name).listen(
      (_) => _reload(),
      onError: (Object _) => _reopen(watch, subGeneration),
      onDone: () => _reopen(watch, subGeneration),
    );
  }

  // A stream that ends or breaks (a proxy idle-close, a silo roll) is reopened after a pause;
  // the pause reads the node once so nothing published meanwhile is missed.
  void _reopen(Stream<void> Function(String, String) watch, int subGeneration) {
    if (!mounted || subGeneration != _watchGeneration || _resubscribe != null) {
      return;
    }
    _resubscribe = Timer(widget.refreshEvery, () {
      _resubscribe = null;
      if (!mounted || subGeneration != _watchGeneration) return;
      _reload();
      _subscribe(watch, _watchedKind, _watchedName);
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
    if (moved || old.watch != widget.watch) {
      _timer?.cancel();
      _timer = null;
      final watch = _effectiveWatch;
      if (watch == null) {
        _resubscribe?.cancel();
        _resubscribe = null;
        _watchGeneration++;
        _watch?.cancel();
        _watch = null;
        _watchedKind = '';
        _watchedName = '';
        _timer = Timer.periodic(widget.refreshEvery, (_) => _reload());
      } else {
        final initial = _initialWatchTarget();
        _subscribe(watch, initial.$1, initial.$2);
      }
    }
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
      final data = await _effectiveLoad(widget.kind, widget.name);
      if (mounted && generation == _generation && version == _events.version) {
        setState(() {
          _data = data;
          _error = null;
        });
        _syncWatchForData(data);
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
    final send = _effectiveOnAction;
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
    watchSignal: widget.watchSignal,
    callNeuron: widget.callNeuron,
    ancestors: {...widget.ancestors, '${widget.kind}:${widget.name}'},
    childDepth: widget.childDepth,
    childAncestors: widget.childAncestors,
    revision: widget.revision,
    enabled: widget.enabled,
    transcribe: widget.transcribe,
    talk: widget.talk,
    speak: widget.speak,
    storeSecret: widget.storeSecret,
    embedded: true,
  );

  /// Dedicated surface kinds hosted via `ViewHost` (`viewport`, `webbrowser`).
  /// Live `NeuronRef` view kinds are handled via [isEmbedRefKind];
  /// `confirmation` is never referable.
  static const _referable = {'viewport', 'webbrowser'};

  Widget? _buildReference(
    String rawKind,
    String rawName,
    String identity,
    Set<String> activeChildAncestors,
    Map<String, dynamic> props,
  ) {
    final kind = rawKind.trim().toLowerCase();
    if (kind == 'confirmation') return null;

    if (_referable.contains(kind)) {
      if (NodeContracts.tryResolve(kind) == null) return null;
      final childHost = _child({'kind': kind, 'name': rawName});
      return LayoutBuilder(
        builder: (context, constraints) =>
            constraints.hasBoundedHeight && constraints.maxHeight.isFinite
            ? SizedBox.expand(child: childHost)
            : SizedBox(height: 480, child: childHost),
      );
    }

    if (!isEmbedRefKind(kind)) return null;

    final trimmedUri = rawName.trim();
    if (trimmedUri.isEmpty) return null;

    // A `child` naming a bare key is this view's child: it is read and acted on through this view's root
    // app with its key path (`{root}/children/{path}`), never by the child's own neuron name (the kernel
    // sends a screen's children by key only). A child naming a whole address (`local/units/bank`) embeds
    // that neuron's own view by name.
    final isChild = isScreenChild(kind, trimmedUri);
    final root =
        widget.actNeuron ??
        (widget.name.endsWith('/view')
            ? widget.name.substring(0, widget.name.length - 5)
            : widget.name);
    final childKey = isChild ? trimmedUri : '';
    if (isChild && !isChildKey(childKey)) return null;
    final childPath = !isChild
        ? ''
        : widget.childPath.isEmpty
        ? childKey
        : '${widget.childPath}/$childKey';
    final resolvedName = isChild
        ? '$root/children/$childPath'
        : resolveEmbedTargetName(
            parentView: widget.name,
            refKind: kind,
            neuronUri: trimmedUri,
          );
    if (resolvedName.isEmpty || NodeContracts.tryResolve('view') == null) {
      return null;
    }

    final targetIdentity = '${isChild ? 'child' : 'view'}:$resolvedName';
    if (activeChildAncestors.contains(resolvedName) ||
        (!isChild && activeChildAncestors.contains(trimmedUri)) ||
        widget.ancestors.contains(targetIdentity)) {
      return EmbedGuardNotice(
        neuronUri: rawName,
        message: 'This component cannot be displayed.',
      );
    }
    if (widget.childDepth >= maxEmbedDepth) {
      return EmbedGuardNotice(
        neuronUri: rawName,
        message: 'Nested view limit reached.',
      );
    }

    return ViewHost(
      key: ValueKey('child:$resolvedName'),
      actNeuron: isChild ? root : null,
      childPath: childPath,
      childProps: props,
      kind: isChild ? 'child' : 'view',
      name: resolvedName,
      load: widget.load,
      onActivate: widget.onActivate,
      onAction: widget.onAction,
      knowsApp: widget.knowsApp,
      events: _events,
      refreshEvery: widget.refreshEvery,
      watchSignal: widget.watchSignal,
      callNeuron: widget.callNeuron,
      // A child has no watchable name of its own: it reloads with its parent's events and on the refresh timer.
      watch: isChild ? null : widget.watch,
      ancestors: {...widget.ancestors, identity},
      childDepth: widget.childDepth + 1,
      childAncestors: {...activeChildAncestors, resolvedName},
      revision: widget.revision,
      enabled: widget.enabled,
      transcribe: widget.transcribe,
      talk: widget.talk,
      speak: widget.speak,
      storeSecret: widget.storeSecret,
      embedded: true,
      inlineChild: true,
    );
  }

  @override
  Widget build(BuildContext context) {
    final content = KeyedSubtree(key: _focusKey, child: _content(context));
    // A child names its key path in the semantics tree, so a browser finds it as
    // `flt-semantics-identifier="child:<path>"` without reading widget keys.
    return widget.childPath.isEmpty
        ? content
        : Semantics(
            identifier: 'child:${widget.childPath}',
            container: true,
            explicitChildNodes: true,
            child: content,
          );
  }

  Widget _content(BuildContext context) {
    final identity = '${widget.kind}:${widget.name}';
    if (widget.ancestors.contains(identity) || widget.ancestors.length >= 16) {
      return widget.inlineChild
          ? EmbedGuardNotice(
              neuronUri: widget.name,
              message: 'This component cannot be displayed.',
            )
          : const Center(child: Text('This component cannot be displayed.'));
    }
    if (widget.inlineChild && widget.childDepth > maxEmbedDepth) {
      return EmbedGuardNotice(
        neuronUri: widget.name,
        message: 'Nested view limit reached.',
      );
    }

    final data = _data;
    if (data == null) {
      return _ReadState(
        viewName: widget.name,
        inlineChild: widget.inlineChild,
        error: _error,
        state: WindowState(
          _error == null ? WindowStatus.loading : WindowStatus.failed,
        ),
        onRetry: _error == null ? null : _reload,
      );
    }
    final action = _effectiveOnAction;
    final enabled =
        widget.enabled &&
        action != null &&
        !(widget.kind == 'confirmation' && widget.embedded);
    switch (widget.kind) {
      case 'view':
      case 'child':
      case 'confirmation':
        // An app view reads as {generation, view}; the confirmation inbox as the view itself.
        final rawView = data['view'] is Map ? data['view'] as Map : data;
        if (rawView['root'] is! Map) {
          return Center(
            child: Semantics(
              liveRegion: true,
              child: const Text('Preparing this view'),
            ),
          );
        }
        final normalizedView = _normalizeViewJson(rawView);
        final buttonHints = _extractButtonHints(normalizedView);
        final activeChildAncestors = {...widget.childAncestors, widget.name};
        final ctx = NodeContext(
          view: widget.name,
          generation: data['generation'] is num
              ? (data['generation'] as num).toInt()
              : 0,
          send: action == null ? null : _dispatch,
          load: _effectiveLoad,
          reload: _reload,
          watch: _effectiveWatch,
          watchSignal: _effectiveWatchSignal,
          callNeuron: _effectiveCallNeuron,
          onActivate: _effectiveOnActivate,
          knowsApp: _effectiveKnowsApp,
          reference: (kind, name, props) => _buildReference(
            kind,
            name,
            identity,
            activeChildAncestors,
            props,
          ),
          confirmation: widget.kind == 'confirmation' && !widget.embedded,
          enabled: enabled,
          childDepth: widget.childDepth,
          childAncestors: activeChildAncestors,
          actNeuron: widget.actNeuron,
          childPath: widget.childPath,
          childProps: widget.childProps,
          buttonHints: buttonHints,
          transcribe: _effectiveTranscribe,
          talk: _effectiveTalk,
          speak: _effectiveSpeak,
          storeSecret: _effectiveStoreSecret,
        );
        ViewHost.debugOnContext?.call(widget.name, ctx);
        final appView = AppView.fromJson(normalizedView);
        final fills = NodeRenderer.fills(appView.root);
        final renderedView = FocusTraversalGroup(
          policy: OrderedTraversalPolicy(),
          child: NodeRenderer.view(appView, ctx),
        );
        final rendered = fills
            ? LayoutBuilder(
                builder: (context, constraints) => constraints.hasBoundedHeight
                    ? SizedBox.expand(child: renderedView)
                    : SizedBox(
                        height: widget.inlineChild ? 420 : 560,
                        child: renderedView,
                      ),
              )
            : widget.inlineChild
            ? renderedView
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
        final send = action;
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
          // Browser reports go to the brain's browser routes
          // (IntoChatOSUiClient.webBrowser), not to the UI event route.
          onConnect: (port, sessionId) => send({
            'kind': 'webbrowser',
            'action': 'connect',
            'name': widget.name,
            'port': port,
            'sessionId': sessionId,
          }),
          onDisconnect: (sessionId) => send({
            'kind': 'webbrowser',
            'action': 'disconnect',
            'name': widget.name,
            'sessionId': sessionId,
          }),
          onUnavailable: (reason) => send({
            'kind': 'webbrowser',
            'action': 'unavailable',
            'name': widget.name,
            'reason': reason,
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
    _watchGeneration++;
    _watch?.cancel();
    _events.removeListener(_reload);
    if (widget.events == null) _events.dispose();
    super.dispose();
  }
}

/// Normalizes optional inline child/child fields on conversation messages so
/// `MessageNode.fromJson` populates `cards` seamlessly for inline UI kit nodes
/// and `NeuronRef` composition (`UK-18` readiness).
Map<String, dynamic> _normalizeViewJson(Map raw) {
  Object? walk(Object? value, {bool inMessages = false}) {
    if (value is List) {
      return [for (final item in value) walk(item, inMessages: inMessages)];
    }
    if (value is! Map) return value;
    final map = <String, dynamic>{
      for (final entry in value.entries)
        '${entry.key}': walk(
          entry.value,
          inMessages: '${entry.key}' == 'messages',
        ),
    };
    if (inMessages) {
      final merged = <Object?>[
        if (map['cards'] is List) ...(map['cards'] as List),
        if (map['children'] is List) ...(map['children'] as List),
        if (map['nodes'] is List) ...(map['nodes'] as List),
        if (map['slot'] is Map) map['slot'],
      ];
      if (merged.isNotEmpty && map['cards'] == null) {
        map['cards'] = merged;
      } else if (merged.length >
          (map['cards'] is List ? (map['cards'] as List).length : 0)) {
        map['cards'] = merged;
      }
    }
    return map;
  }

  return walk(raw) as Map<String, dynamic>;
}

/// Extracts optional `.Guard(rule)` / tooltip / hint strings keyed by button `key`
/// from raw view JSON without requiring manual edits to `generated/node_models.dart`.
Map<String, String> _extractButtonHints(Map<String, dynamic> viewJson) {
  final hints = <String, String>{};
  const hintKeys = [
    'tooltip',
    'disabledReason',
    'guardReason',
    'reason',
    'hint',
    'subtitle',
  ];

  void walk(Object? node) {
    if (node is List) {
      for (final item in node) {
        walk(item);
      }
      return;
    }
    if (node is! Map) return;
    final key = node['key'];
    final looksLikeButton =
        node['kind'] == 'button' ||
        (key is String && key.isNotEmpty && node.containsKey('label'));
    if (looksLikeButton && key is String && key.isNotEmpty) {
      for (final hk in hintKeys) {
        final raw = node[hk];
        if (raw is String && raw.trim().isNotEmpty) {
          hints[key] = raw.trim();
          break;
        }
      }
    }
    for (final value in node.values) {
      walk(value);
    }
  }

  walk(viewJson['root']);
  return hints;
}

/// Extracts optional activation `key` strings for `chart` and `dataTable` nodes
/// from raw view JSON without altering `generated/node_models.dart`.
class _ReadState extends StatelessWidget {
  const _ReadState({
    required this.viewName,
    required this.state,
    this.inlineChild = false,
    this.error,
    this.onRetry,
  });

  final String viewName;
  final WindowState state;
  final bool inlineChild;
  final Object? error;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    if (state.status == WindowStatus.loading) {
      final skeleton = EmbedSkeleton(compact: inlineChild);
      return inlineChild
          ? skeleton
          : Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 420),
                child: skeleton,
              ),
            );
    }

    final errorNode = ErrorLineNode(
      what: 'Could not load',
      detail: 'This component could not be loaded.',
      action: onRetry == null
          ? null
          : const ButtonNode(
              key: 'retry',
              label: 'Retry',
              command: null,
              intent: null,
              enabled: true,
              tone: 'neutral',
              automationName: 'Retry',
              tooltip: null,
              visible: true,
              style: 'secondary',
              icon: null,
              small: true,
            ),
    );
    // The retry is the host's own action, not a command of any view: the line
    // runs it directly, and its context sends nothing.
    final retryCtx = NodeContext(
      view: viewName,
      send: onRetry == null ? null : (_) async => null,
    );
    final line = ErrorLineView(
      node: errorNode,
      ctx: retryCtx,
      onAction: onRetry,
    );
    return inlineChild ? line : Center(child: line);
  }
}
