import '../components/button/ui_neuron_select.dart';
import '../components/graph/ui_graph.dart';
import '../components/workspace/ui_workspace_components.dart';
import '../components/button/ui_neuron_button.dart';
import '../components/kit/ui_kit_field.dart';
import '../components/kit/kit_toast.dart';
import '../components/kit/ui_kit_surface.dart';
import '../components/kit/ui_kit_switch.dart';
import '../design/app_tile.dart';
import '../design/brain_scene_3d.dart';
import '../focus_registry.dart';

import '../design/cards/cards.dart';
import '../design/controls.dart';
import '../design/copy/ino_copy.dart';
import '../controls/ic_breadcrumb_bar.dart';
import '../controls/ic_files_finder_view.dart';
import '../design/tokens.dart';
import '../gallery/ui_gallery_screen.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

import 'dart:async';
import 'dart:convert';
import 'dart:math';

import 'package:file_picker/file_picker.dart';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:gpt_markdown/gpt_markdown.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart' hide TableColumn;
import 'package:lucide_icons_flutter/lucide_icons.dart';
import 'package:markdraw/markdraw.dart' as md;
import 'package:url_launcher/url_launcher.dart';
import 'package:uuid/uuid.dart';

import '../voice_input/ui_voice_input.dart';
import '../chat/chat_run_link.dart';
import '../chat/ui_chat_transcript.dart';
import '../components/kit/ui_kit_button.dart';
import '../chat/ui_composer.dart';
import '../components/browser/embedded_browser_stub.dart'
    if (dart.library.io) '../components/browser/embedded_browser_windows.dart'
    if (dart.library.js_interop) '../components/browser/embedded_browser_web.dart';
import '../components/browser/ui_web_browser.dart';
import '../components/chart/ui_chart.dart';
import '../components/expander/ui_expander.dart';
import '../components/image/ui_image.dart';
import '../components/infobar/ui_info_bar.dart';
import '../components/progress/ui_progress.dart';
import '../components/toggle/ui_toggle.dart';
import '../entries/entries_client.dart';
import '../entries/ui_entry_control.dart';
import '../flame/scene_view.dart';
import '../models/ui_part.dart';
import '../preferences/local_preferences.dart';
import '../preferences/ui_preference_control.dart';
import 'renderer_registry.dart';
import 'ui_collection_view.dart';
import 'value_nodes/view_avatar.dart';
import 'value_nodes/view_badge.dart';
import 'value_nodes/view_divider.dart';
import 'value_nodes/view_list.dart';
import 'value_nodes/view_menu.dart';
import 'value_nodes/view_modal.dart';
import 'value_nodes/view_tabs.dart';
import 'value_nodes/view_toast.dart';

part 'immutable_view.dart';
part 'neuron_legacy_renderers.dart';
part 'kit/ui_kit_studio_surfaces.dart';
part 'kit/ui_kit_rich_cards.dart';
part 'kit/ui_kit_files_adapter.dart';
part 'kit/ui_kit_settings_adapter.dart';
part 'kit/ui_kit_ino_workspace.dart';
part 'kit/ui_kit_ino_widgets.dart';

typedef NeuronLoader = Future<Map<String, dynamic>> Function(
  String kind,
  String name,
);
typedef SecretSaver = Future<String?> Function(String fieldName, String value);

/// One ordered event channel for a composed tree. A button is processed only
/// after preceding field edits, regardless of network latency or input focus.
class NeuronEventQueue extends ChangeNotifier {
  Future<void> _tail = Future.value();
  int version = 0, pending = 0;
  bool _disposed = false;
  Future<void> flush() => _tail;
  Future<void> dispatch(
    Future<void> Function(Map<String, dynamic>) send,
    Map<String, dynamic> event,
  ) {
    version++;
    pending++;
    final operation = _tail.then((_) => send(event));
    _tail = operation.catchError((Object _) {});
    return operation.whenComplete(() {
      pending--;
      version++;
      if (!_disposed && pending == 0) notifyListeners();
    });
  }

  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}

class NeuronView extends StatefulWidget {
  const NeuronView({
    super.key,
    required this.kind,
    required this.name,
    required this.load,
    this.onActivate,
    this.onAction,
    this.imageBuilder,
    this.voiceBuilder,
    this.secretSaver,
    this.ancestors = const {},
    this.revision = 0,
    this.enabled = true,
    this.events,
    this.refreshEvery = const Duration(seconds: 2),
    this.watch,
    this.scenes,
    this.entries,
    this.localPreferences,
  });
  final String kind, name;
  final NeuronLoader load;
  final ValueChanged<Map<String, dynamic>>? onActivate;
  final Future<void> Function(Map<String, dynamic>)? onAction;
  final Widget Function(Map<String, dynamic>)? imageBuilder;
  final Widget Function(String name, Map<String, dynamic> state)? voiceBuilder;
  final SecretSaver? secretSaver;
  final Set<String> ancestors;
  final int revision;
  final bool enabled;
  final NeuronEventQueue? events;
  final Duration refreshEvery;

  /// The node's change events. With a watch the view never polls: it reads once on mount and
  /// once per event. Without one it falls back to [refreshEvery].
  final Stream<void> Function(String kind, String name)? watch;

  /// Reads `scene` children over the brain's Flame routes; without it a scene draws from [load].
  final SceneClient? scenes;

  /// The brain's entries over the person's session, for `entrycontrol`. Without it the control cannot save.
  final EntriesClient Function(String brainId)? entries;

  /// This device's preferences, for a `preference` neuron with a `local*` store. Without it those cannot save.
  final LocalPreferences? localPreferences;
  @override
  State<NeuronView> createState() => _NeuronViewState();
}

class _NeuronViewState extends State<NeuronView> {
  Map<String, dynamic>? _data;
  SceneState? _sceneState;
  bool _sceneRecovery = true, _animateSceneChanges = false;
  Object? _error;
  String? selection;
  Timer? _timer;
  StreamSubscription<void>? _watch;
  bool _loading = false, _reloadAgain = false;
  int _generation = 0;
  late final NeuronEventQueue _events = widget.events ?? NeuronEventQueue();
  final GlobalKey _focusKey = GlobalKey();
  late String _focusTarget = '${widget.kind}:${widget.name}';

  @override
  void initState() {
    super.initState();
    FocusRegistry.instance.register(_focusTarget, _focusKey);
    _events.addListener(_changed);
    ChatRunLinks.follows.addListener(_followsChanged);
    _reload();
    final watch = widget.watch;
    if (watch == null) {
      _timer = Timer.periodic(widget.refreshEvery, (_) => _reload());
    } else {
      _subscribe(watch);
    }
  }

  Timer? _resubscribe;

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
    _sceneRecovery = true;
    _resubscribe = Timer(widget.refreshEvery, () {
      _resubscribe = null;
      if (!mounted) return;
      _reload();
      _subscribe(watch);
    });
  }

  void _changed() {
    _reload();
  }

  @override
  void didUpdateWidget(NeuronView old) {
    super.didUpdateWidget(old);
    if (old.name != widget.name || old.kind != widget.kind) {
      _generation++;
      _data = null;
      _sceneState = null;
      _sceneRecovery = true;
      _error = null;
      FocusRegistry.instance.unregister(_focusTarget, _focusKey);
      _focusTarget = '${widget.kind}:${widget.name}';
      FocusRegistry.instance.register(_focusTarget, _focusKey);
    }
    if (old.name != widget.name ||
        old.kind != widget.kind ||
        old.revision != widget.revision) {
      _reload();
    }
    if ((old.name != widget.name || old.kind != widget.kind) &&
        widget.watch != null) {
      _subscribe(widget.watch!);
    }
  }

  bool get _scenesReadThemselves =>
      widget.kind == 'scene' && widget.scenes != null;

  Future<void> _reload() async {
    if (_events.pending > 0) return;
    if (_loading) {
      _reloadAgain = true;
      return;
    }
    _loading = true;
    if (_error != null) setState(() => _error = null);
    final generation = _generation;
    final version = _events.version;
    try {
      final scene = _scenesReadThemselves
          ? await widget.scenes!.read(widget.name)
          : null;
      final data = scene == null
          ? await widget.load(widget.kind, widget.name)
          : null;
      if (mounted && generation == _generation && version == _events.version) {
        setState(() {
          _data = data;
          _sceneState = scene;
          _animateSceneChanges = !_sceneRecovery;
          _sceneRecovery = false;
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
          _sceneState = null;
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

  Future<void> _dispatch(Map<String, dynamic> event) async {
    final send = widget.onAction;
    if (!widget.enabled || send == null) return;
    try {
      await _events.dispatch(send, event);
    } catch (error) {
      if (mounted) {
        ScaffoldMessenger.maybeOf(context)?.showSnackBar(
          SnackBar(content: Text('Could not save change: $error')),
        );
      }
      rethrow;
    }
  }

  Widget _layout(
    Map<String, dynamic> definition,
    List<Widget> children,
    List rawChildren,
  ) {
    if (children.isEmpty) return const SizedBox.shrink();
    final gap = (definition['gap'] as num? ?? 0).toDouble().clamp(0.0, 128.0);
    if (definition['mode'] == 'stack') {
      return Stack(fit: StackFit.expand, children: children);
    }
    if (definition['mode'] == 'list') {
      return LayoutBuilder(
        builder: (context, constraints) {
          final column = Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: gap,
            children: children,
          );
          return constraints.hasBoundedHeight
              ? _NeuronList(
                  followEnd: definition['followEnd'] == true,
                  child: column,
                )
              : column;
        },
      );
    }
    final horizontal =
        definition['mode'] == 'row' || definition['mode'] == 'split';
    final stretch = definition['stretch'] == true;
    Widget buildFlex({required bool boundedCross}) => Flex(
      direction: horizontal ? Axis.horizontal : Axis.vertical,
      crossAxisAlignment: horizontal
          ? (stretch && boundedCross
                ? CrossAxisAlignment.stretch
                : stretch
                ? CrossAxisAlignment.start
                : CrossAxisAlignment.center)
          : CrossAxisAlignment.stretch,
      spacing: gap,
      children: [
        for (var i = 0; i < children.length; i++)
          if ((definition['extents'] as List?)?.elementAtOrNull(i)
              case final num extent when extent > 1)
            SizedBox(
              key: children[i].key,
              width: horizontal ? extent.toDouble() : null,
              height: horizontal ? null : extent.toDouble(),
              child: children[i],
            )
          else if (!horizontal &&
              rawChildren[i] is Map &&
              const {
                'voiceinput',
                'fileinput',
              }.contains((rawChildren[i] as Map)['kind']))
            children[i]
          else
            Expanded(key: children[i].key, child: children[i]),
      ],
    );
    final extents = definition['extents'] as List? ?? const [];
    if (!horizontal && !extents.any((extent) => extent is num && extent > 1)) {
      return buildFlex(boundedCross: true);
    }
    final minimumExtent =
        gap * (children.length - 1) +
        List.generate(children.length, (index) {
          final extent = extents.elementAtOrNull(index);
          return extent is num && extent > 1 ? extent.toDouble() : 96.0;
        }).fold(0.0, (sum, extent) => sum + extent);
    return LayoutBuilder(
      builder: (context, constraints) {
        final flex = buildFlex(
          boundedCross: horizontal
              ? constraints.hasBoundedHeight
              : constraints.hasBoundedWidth,
        );
        if (horizontal
            ? !constraints.hasBoundedWidth
            : !constraints.hasBoundedHeight) {
          return flex;
        }
        final available = horizontal
            ? constraints.maxWidth
            : constraints.maxHeight;
        final extent = available < minimumExtent ? minimumExtent : available;
        return SingleChildScrollView(
          primary: false,
          scrollDirection: horizontal ? Axis.horizontal : Axis.vertical,
          child: SizedBox(
            width: horizontal ? extent : null,
            height: horizontal ? null : extent,
            child: flex,
          ),
        );
      },
    );
  }

  Future<void> _button(Map<String, dynamic> definition) async {
    FocusScope.of(context).unfocus();
    try {
      if (definition['activation'] case final String json) {
        await _events.flush();
        if (!mounted) return;
        final activation = Map<String, dynamic>.from(jsonDecode(json) as Map);
        final uri = Uri.tryParse('${activation['url'] ?? ''}');
        if (uri != null && const {'http', 'https'}.contains(uri.scheme)) {
          await launchUrl(uri);
        } else {
          widget.onActivate?.call(activation);
        }
      } else {
        await _dispatch({
          'kind': 'button',
          'name': widget.name,
          'action': definition['action'],
        });
      }
    } catch (_) {
      /* Dispatch presents the failure and keeps the projection. */
    }
  }

  static String _text(Object? value, [String empty = '']) =>
      value is String ? value : empty;
  static double _number(Object? value) => switch (value) {
    num n => n.toDouble(),
    String t => double.tryParse(t) ?? 0,
    _ => 0,
  };

  Future<void> _act(String kind, String action) async {
    try {
      await _dispatch({'kind': kind, 'name': widget.name, 'action': action});
    } catch (_) {
      /* Presented by _dispatch's status path. */
    }
  }

  Future<void> _file() async {
    try {
      final result = await FilePicker.platform.pickFiles(withData: true);
      if (!mounted || result == null || result.files.isEmpty) return;
      final file = result.files.first;
      if (file.bytes == null) return;
      await _dispatch({
        'kind': 'fileinput',
        'name': widget.name,
        'value': utf8.decode(file.bytes!, allowMalformed: true),
        'field': file.name,
      });
    } catch (error) {
      if (mounted) {
        ScaffoldMessenger.maybeOf(context)?.showSnackBar(
          SnackBar(content: Text('Could not attach file: $error')),
        );
      }
    }
  }

  Widget _child(Map child, {Key? key}) => NeuronView(
    key: key ?? ValueKey('${child['kind']}:${child['name']}'),
    kind: child['kind'] as String,
    name: child['name'] as String,
    load: widget.load,
    onActivate: widget.onActivate,
    onAction: widget.onAction,
    imageBuilder: widget.imageBuilder,
    voiceBuilder: widget.voiceBuilder,
    secretSaver: widget.secretSaver,
    events: _events,
    refreshEvery: widget.refreshEvery,
    watch: widget.watch,
    scenes: widget.scenes,
    entries: widget.entries,
    localPreferences: widget.localPreferences,
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
    if (_scenesReadThemselves) {
      final state = _sceneState;
      if (state == null) {
        return _ComponentReadState(
          state: WindowState(
            _error == null ? WindowStatus.loading : WindowStatus.failed,
          ),
          onRetry: _error == null ? null : _reload,
        );
      }
      if (widget.ancestors.isEmpty && state.revision == 0) {
        return Semantics(
          liveRegion: true,
          child: const Center(child: Text('Preparing this view')),
        );
      }
      return SceneView(
        sceneKey: widget.name,
        client: widget.scenes,
        state: state,
        animateChanges: _animateSceneChanges,
      );
    }
    final studioMatch = RegExp(
      r'/apps/([a-z0-9_-]+)/view$',
    ).firstMatch(widget.name);
    final isStudioView =
        widget.kind == 'view' &&
        studioMatch != null &&
        IcAppIcon.isArtCraftApp(studioMatch.group(1));
    if (_data == null && !isStudioView) {
      return _ComponentReadState(
        state: WindowState(
          _error == null ? WindowStatus.loading : WindowStatus.failed,
        ),
        onRetry: _error == null ? null : _reload,
      );
    }
    final data = _data ?? const <String, dynamic>{};
    if (widget.ancestors.isEmpty &&
        !isStudioView &&
        !widget.name.endsWith('/apps/gallery/view') &&
        !widget.name.endsWith('/apps/brain/view') &&
        (data['version'] ?? data['revision']) == 0) {
      return Center(
        child: Semantics(
          liveRegion: true,
          child: const Text('Preparing this view'),
        ),
      );
    }
    final definition = Map<String, dynamic>.from(
      data['definition'] as Map? ?? data,
    );
    final rawChildren =
        (definition['children'] is List
                ? definition['children'] as List
                : const [])
            .take(128)
            .toList();
    final children = [
      for (final child in rawChildren)
        if (child is Map && child['kind'] is String && child['name'] is String)
          _child(child)
        else
          const Text('This component cannot be displayed.'),
    ];
    final enabled = widget.enabled && definition['enabled'] != false;
    switch (widget.kind) {
      case 'view':
      case 'confirmation':
        final rawRoot = data['root'];
        if (rawRoot is! Map && !isStudioView) return const SizedBox.shrink();
        final root = rawRoot is Map
            ? rawRoot
            : <String, dynamic>{'key': 'view', 'kind': 2, 'children': const []};
        if (widget.kind == 'view' &&
            widget.name.endsWith('/apps/gallery/view') &&
            (root['children'] is! List || (root['children'] as List).isEmpty)) {
          return const UiGalleryScreen();
        }
        if (widget.kind == 'view' &&
            widget.name.endsWith('/apps/brain/view') &&
            root['key'] != 'app-body' &&
            (root['children'] is! List || (root['children'] as List).isEmpty)) {
          return const BrainScene3D(
            selectedId: 'ino',
            ambientOpacity: 0.98,
            showHud: true,
            interactive: true,
          );
        }
        return ImmutableView(
          name: widget.name,
          generation: (data['generation'] as num?)?.toInt() ?? 0,
          root: root,
          enabled: enabled && widget.onAction != null,
          dispatch: widget.kind == 'confirmation'
              ? (event) => _dispatch({...event, 'kind': 'confirmation'})
              : _dispatch,
          markdown: _markdown,
          reference: (node, key) => _child(node, key: key),
          layout: _layout,
          localPreferences: widget.localPreferences,
          onActivate: widget.onActivate,
        );
      case 'viewport':
        final root = data['root'];
        if (root is Map && root['kind'] is String && root['name'] is String) {
          final targetName = root['name'] as String;
          final mainMatch = RegExp(
            r'/apps/([a-z0-9_-]+)/main$',
          ).firstMatch(targetName);
          if (mainMatch != null &&
              IcAppIcon.isArtCraftApp(mainMatch.group(1))) {
            final viewRef = <String, dynamic>{
              'kind': 'view',
              'name': targetName.replaceFirst(RegExp(r'/main$'), '/view'),
            };
            return UiMotion(
              contentKey: 'view:${viewRef['name']}',
              child: _child(viewRef),
            );
          }
          return UiMotion(
            contentKey: '${root['kind']}:${root['name']}',
            child: _child(root),
          );
        }
        return Center(child: Text('${data['error'] ?? ''}'));
      default:
        return _buildLegacyNeuronKind(
          context,
          data,
          definition,
          children,
          rawChildren,
          enabled,
        );
    }
  }

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
    _events.removeListener(_changed);
    if (widget.events == null) _events.dispose();
    super.dispose();
  }
}

/// Scroll behavior is declared by the layout, independent of its contents.
class _NeuronList extends StatefulWidget {
  const _NeuronList({required this.followEnd, required this.child});
  final bool followEnd;
  final Widget child;
  @override
  State<_NeuronList> createState() => _NeuronListState();
}

class _NeuronListState extends State<_NeuronList> {
  final _scroll = ScrollController();
  bool _following = true, _scheduled = false;
  void _follow() {
    if (_scheduled || !widget.followEnd || !_following) return;
    _scheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _scheduled = false;
      if (!mounted || !_scroll.hasClients || !_following || !widget.followEnd) {
        return;
      }
      final end = _scroll.position.maxScrollExtent;
      if ((end - _scroll.offset).abs() > 1) _scroll.jumpTo(end);
    });
  }

  @override
  Widget build(BuildContext context) =>
      NotificationListener<ScrollMetricsNotification>(
        onNotification: (notification) {
          if (notification.depth == 0) _follow();
          return false;
        },
        child: NotificationListener<ScrollNotification>(
          onNotification: (notification) {
            if (notification.depth == 0 &&
                (notification is UserScrollNotification ||
                    notification is ScrollUpdateNotification &&
                        notification.dragDetails != null)) {
              _following = notification.metrics.extentAfter < 48;
            }
            return false;
          },
          child: SingleChildScrollView(
            controller: _scroll,
            child: widget.child,
          ),
        ),
      );
  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }
}

class _ComponentReadState extends StatelessWidget {
  const _ComponentReadState({required this.state, this.onRetry});
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
