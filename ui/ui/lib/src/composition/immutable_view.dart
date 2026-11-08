part of 'neuron_view.dart';

/// Inline presentation values never acquire their own load or watch lifecycle.
class ImmutableView extends StatelessWidget {
  const ImmutableView({
    super.key,
    required this.name,
    required this.generation,
    required this.root,
    required this.enabled,
    required this.dispatch,
    required this.markdown,
    required this.reference,
    required this.layout,
    this.localPreferences,
    this.onActivate,
  });
  final String name;
  final int generation;
  final Map root;
  final bool enabled;
  final Future<void> Function(Map<String, dynamic>) dispatch;
  final Widget Function(String) markdown;
  final Widget Function(Map, Key) reference;
  final Widget Function(Map<String, dynamic>, List<Widget>, List) layout;
  final LocalPreferences? localPreferences;
  final ValueChanged<Map<String, dynamic>>? onActivate;

  static String _operationId() => const Uuid().v4();

  static Map<String, dynamic>? _tryParseUiMeta(Object? detail) {
    if (detail is! String || !detail.startsWith('{"_ui":')) return null;
    try {
      final decoded = jsonDecode(detail);
      return decoded is Map ? Map<String, dynamic>.from(decoded) : null;
    } catch (_) {
      return null;
    }
  }

  static CardSource _cardSource(BuildContext context, String sourceName) =>
      CardSource(
        sourceName,
        Icon(Icons.auto_awesome, size: 14, color: UiColors.of(context).accent),
      );

  /// Parses an `os://` or shorthand action string into a normalized OS URI
  /// activation map, or returns `null` if [action] is not an OS URI shortcut.
  static Map<String, dynamic>? tryParseOsUriActivation(String action) {
    if (action.startsWith('os://')) {
      final uri = Uri.tryParse(action);
      return {
        'kind': 'os_uri',
        'uri': action,
        'host': uri?.host ?? '',
        'target': uri?.pathSegments.firstOrNull ?? '',
        'query': uri?.queryParameters ?? const <String, String>{},
      };
    }
    if (action.endsWith(':open-settings')) {
      return const {
        'kind': 'os_uri',
        'uri': 'os://window/open?app=settings&section=connections',
        'host': 'window',
        'target': 'open',
        'query': {'app': 'settings', 'section': 'connections'},
      };
    }
    if (action == 'ask-ino' || action.endsWith(':ask-ino')) {
      return const {
        'kind': 'os_uri',
        'uri': 'os://window/open?app=ino',
        'host': 'window',
        'target': 'open',
        'query': {'app': 'ino'},
      };
    }
    return null;
  }

  /// The one button dispatch for every kind; null when it cannot act.
  /// A `copy:` action is handled here: it copies its payload and dispatches nothing.
  VoidCallback? _dispatchButton(BuildContext context, Map node) =>
      enabled && node['enabled'] != false && node['action'] is String
      ? () {
          FocusManager.instance.primaryFocus?.unfocus();
          final action = node['action'] as String;
          if (action.startsWith('copy:')) {
            unawaited(
              Clipboard.setData(ClipboardData(text: action.substring(5))).then(
                (_) {
                  if (context.mounted) showKitToast(context, 'Link copied.');
                },
                onError: (Object _) {
                  if (context.mounted) {
                    showKitToast(
                      context,
                      'Could not copy the link.',
                      tone: UiTone.error,
                    );
                  }
                },
              ),
            );
            return;
          }
          if (onActivate != null) {
            final osActivation = tryParseOsUriActivation(action);
            if (osActivation != null) {
              onActivate!(osActivation);
              if (action.startsWith('os://')) return;
            }
          }
          if (action.startsWith('switch-identity:') &&
              localPreferences != null) {
            final parts = action.split(':');
            if (parts.length > 1 && parts[1].isNotEmpty) {
              unawaited(
                localPreferences!.write(
                  PreferenceStore.localTheme,
                  Uri.decodeComponent(parts[1]),
                ),
              );
            }
          }
          unawaited(
            dispatch({
              'kind': 'view',
              'name': name,
              'revision': generation,
              'field': node['key'],
              'action': node['action'],
              'operationId': _operationId(),
            }).catchError((Object _) {}),
          );
        }
      : null;

  Map? _findChildByKeySuffix(List<Map> children, String suffix) {
    for (final child in children) {
      final k = child['key'];
      if (k is String && k.endsWith(suffix)) return child;
    }
    return null;
  }

  VoidCallback? _childAction(
    BuildContext context,
    List<Map> children,
    String suffix,
  ) {
    final child = _findChildByKeySuffix(children, suffix);
    return child != null ? _dispatchButton(context, child) : null;
  }

  Widget _node(
    BuildContext context,
    Map node,
    int depth, {
    bool slotted = false,
  }) {
    if (depth > 32) return const Text('This component cannot be displayed.');
    final key = ValueKey('view:$name:${node['key']}');
    final children = (node['children'] as List? ?? const [])
        .whereType<Map>()
        .take(512)
        .toList();
    // Built on demand so a List builds only its visible rows.
    final extents = node['kind'] == 2 || node['kind'] == 3
        ? node['extents'] as List? ?? const []
        : const [];
    late final widgets = [
      for (var i = 0; i < children.length; i++)
        _node(
          context,
          children[i],
          depth + 1,
          slotted: switch (extents.elementAtOrNull(i)) {
            final num extent => extent > 0,
            _ => false,
          },
        ),
    ];
    final text = node['text'] as String? ?? '';
    Widget content;
    switch (node['kind']) {
      case 0:
        final meta = _tryParseUiMeta(node['detail']);
        if (meta != null) {
          final c = UiColors.of(context);
          if ((meta['_ui'] as String? ?? '') == 'heading') {
            final fontSize = switch ((meta['level'] as num?)?.toInt() ?? 2) {
              1 => 22.0,
              2 => 17.0,
              _ => 15.0,
            };
            final subtitle = meta['subtitle'] as String?;
            content = Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  text,
                  style: UiType.displayStyle(
                    fontSize,
                    weight: FontWeight.w700,
                    color: c.strong,
                  ),
                ),
                if (subtitle != null && subtitle.isNotEmpty) ...[
                  const SizedBox(height: 4),
                  Text(subtitle, style: UiType.textStyle(12.5, color: c.muted)),
                ],
              ],
            );
          } else {
            final size = (meta['size'] as num?)?.toDouble() ?? 13.0;
            final color = switch (meta['tone'] as String? ?? 'default') {
              'strong' => c.strong,
              'muted' => c.muted,
              'subtle' => c.subtle,
              'accent' => c.accent,
              'success' => c.done,
              'warning' => c.working,
              'danger' => c.error,
              _ => c.body,
            };
            final style = meta['mono'] == true
                ? UiType.tabularStyle(size, color: color)
                : UiType.textStyle(
                    size,
                    color: color,
                    tnum: meta['tabular'] == true,
                  );
            content = SelectionArea(child: Text(text, style: style));
          }
        } else {
          content = markdown(text);
        }
      case 1:
        final forceCompact = node['detail'] == 'small';
        content = LayoutBuilder(
          builder: (context, constraints) => viewButton(
            node,
            _dispatchButton(context, node),
            compact: forceCompact || constraints.maxHeight < 48,
          ),
        );
        if (!slotted) {
          content = Align(
            alignment: AlignmentDirectional.centerStart,
            heightFactor: 1,
            child: content,
          );
        }
      case 2:
      case 3:
        if (const {'ino-workspace', 'ino-main'}.contains(node['key'])) {
          content = _buildInoChatDesktopView(context, node, children, depth);
          break;
        }
        if (node['kind'] == 2 &&
            node['key'] == 'settings' &&
            children.length >= 2 &&
            children[0]['key'] == 'tabs') {
          content = _buildSettingsDesktopView(
            context,
            node,
            children,
            widgets,
            depth,
          );
          break;
        }
        if (const {
          'files-full',
          'files-main-wrap',
          'files-main',
        }.contains(node['key'])) {
          content = _buildFilesFinderDesktopView(
            context,
            node,
            children,
            depth,
          );
          break;
        }
        if (node['kind'] == 3 &&
            node['key'] == 'view' &&
            children.length == 2 &&
            children[0]['key'] == 'side' &&
            children[1]['key'] == 'root') {
          final c = UiColors.of(context);
          final sideWidth = switch ((node['extents'] as List?)?.elementAtOrNull(
            0,
          )) {
            final num w when w > 1 => w.toDouble(),
            _ => 260.0,
          };
          content = LayoutBuilder(
            builder: (context, constraints) {
              if (constraints.maxWidth < 720) {
                return SingleChildScrollView(
                  padding: const EdgeInsets.all(14),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    spacing: 14,
                    children: [widgets[0], widgets[1]],
                  ),
                );
              }
              return Container(
                color: c.page,
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Container(
                      width: sideWidth,
                      decoration: BoxDecoration(
                        color: c.recessed,
                        border: Border(right: BorderSide(color: c.hairline)),
                      ),
                      child: Padding(
                        padding: const EdgeInsets.all(12),
                        child: widgets[0],
                      ),
                    ),
                    Expanded(
                      child: Padding(
                        padding: const EdgeInsets.fromLTRB(22, 18, 22, 28),
                        child: widgets[1],
                      ),
                    ),
                  ],
                ),
              );
            },
          );
          break;
        }
        if (node['kind'] == 3 && node['key'] == 'breadcrumbs') {
          content = _buildWinUiBreadcrumbBar(context, children, widgets);
          break;
        }
        if (node['kind'] == 3 &&
            children.length == 2 &&
            extents.length == 2 &&
            extents[0] is num &&
            (extents[0] as num) >= 400 &&
            (extents[1] as num? ?? 0) == 0) {
          final splitGap = (node['gap'] as num? ?? 14).toDouble().clamp(
            0.0,
            32.0,
          );
          final trailingWidth = (extents[0] as num) >= 500 ? 284.0 : 360.0;
          content = LayoutBuilder(
            builder: (context, constraints) {
              if (constraints.maxWidth < 760) {
                return Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  spacing: splitGap,
                  children: [widgets[0], widgets[1]],
                );
              }
              return Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(child: widgets[0]),
                  SizedBox(width: splitGap),
                  SizedBox(width: trailingWidth, child: widgets[1]),
                ],
              );
            },
          );
          break;
        }
        if (node['kind'] == 3 &&
            (const {'toolbar', 'navigation'}.contains(node['key']) ||
                ((node['extents'] as List? ?? const []).isEmpty &&
                    children.isNotEmpty &&
                    children.every(
                      (ch) => ch['kind'] == 1 || ch['kind'] == 10,
                    )))) {
          final rowGap = (node['gap'] as num? ?? 6).toDouble().clamp(0.0, 32.0);
          content = Wrap(
            spacing: rowGap,
            runSpacing: rowGap,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: widgets,
          );
          break;
        }
        content = layout(
          {
            'mode': node['kind'] == 3
                ? 'row'
                : (node['extents'] as List? ?? const []).isEmpty
                ? 'list'
                : 'column',
            'extents': node['extents'],
            'gap': node['gap'],
            'followEnd': node['followEnd'],
            'stretch': const {
              'view',
              'split',
              'files-full',
              'browse-split',
            }.contains(node['key']),
          },
          widgets,
          children,
        );
      case 4:
        final meta = _tryParseUiMeta(node['detail']);
        if (meta != null) {
          content = _buildRichCard(context, text, meta, children, widgets);
        } else {
          content = SizedBox(
            width: double.infinity,
            child: UiKitSurface(
              child: SingleChildScrollView(
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (text.isNotEmpty)
                      Text(text, style: Theme.of(context).textTheme.titleLarge),
                    if (node['detail'] case final String detail
                        when detail.isNotEmpty)
                      markdown(detail),
                    ...widgets,
                  ],
                ),
              ),
            ),
          );
        }
      case 5:
        final target = node['reference'];
        if (target is Map &&
            target['kind'] is String &&
            target['name'] is String) {
          return reference(target, key);
        }
        content = const Text('This component cannot be displayed.');
      case 6:
        content = ViewMenu(
          node: node,
          action: (node) => _dispatchButton(context, node),
          reference: (child) => switch (child['reference']) {
            final Map target
                when target['kind'] is String && target['name'] is String =>
              reference(target, ValueKey('view:$name:${child['key']}')),
            _ => const SizedBox.shrink(),
          },
        );
      case 7:
        content = ViewModal(
          node: node,
          children: [
            for (final child in children)
              (
                child,
                child['kind'] == 1
                    ? _node(context, child, depth + 1, slotted: true)
                    : widgets[children.indexOf(child)],
              ),
          ],
          action: (node) => _dispatchButton(context, node),
        );
      case 8:
        content = ViewToast(
          node: node,
          generation: generation,
          action: (node) => _dispatchButton(context, node),
        );
      case 9:
        content = ViewList(
          count: children.length,
          row: (i) => _node(context, children[i], depth + 1),
        );
      case 10:
        final pillMeta = _tryParseUiMeta(node['detail']);
        if (pillMeta != null && pillMeta['_ui'] == 'tagpill') {
          final pillTone = switch ((pillMeta['tone'] as String? ?? 'neutral')
              .toLowerCase()) {
            'accent' => TagTone.accent,
            'done' || 'ok' || 'success' => TagTone.done,
            'working' || 'warn' || 'warning' => TagTone.working,
            'error' || 'danger' => TagTone.error,
            _ => TagTone.neutral,
          };
          final iconSlug = node['icon'] is String
              ? node['icon'] as String
              : null;
          content = TagPill(
            label: text,
            tone: pillTone,
            icon: iconSlug != null ? uiIcon(iconSlug) : null,
          );
        } else {
          content = ViewBadge(
            text: text,
            tone: node['tone'],
            icon: node['icon'] is String ? node['icon'] as String : null,
          );
        }
      case 11:
        content = ViewAvatar(initials: text);
      case 12:
        content = ViewDivider(label: text);
      case 13:
        content = ViewTabs(
          node: node,
          action: (node) => _dispatchButton(context, node),
        );
      default:
        content = const Text('This component cannot be displayed.');
    }
    return KeyedSubtree(key: key, child: content);
  }

  static String _extractStudioUrl(Map node, String fallbackUrl) {
    final detail = node['detail'];
    if (detail is String && detail.contains('"_ui":"webbrowser"')) {
      final meta = _tryParseUiMeta(detail);
      final u = meta?['url'] as String?;
      if (u != null && u.trim().isNotEmpty) return u.trim();
    }
    if (node['key'] == 'browser' && node['text'] is String) {
      final t = (node['text'] as String).trim();
      if (t.startsWith('http://') || t.startsWith('https://')) return t;
    }
    final children = node['children'];
    if (children is List) {
      for (final c in children) {
        if (c is Map) {
          final found = _extractStudioUrl(c, '');
          if (found.isNotEmpty) return found;
        }
      }
    }
    return fallbackUrl;
  }

  @override
  Widget build(BuildContext context) {
    final studioMatch = RegExp(r'/apps/([a-z0-9_-]+)/view$').firstMatch(name);
    final studioAppId = studioMatch?.group(1);
    if (studioAppId != null &&
        IcAppIcon.isArtCraftApp(studioAppId) &&
        root['key'] != 'app-body') {
      final spec = IcAppIcon.resolveSpec(studioAppId);
      final defaultPort = spec.defaultWebPort ?? 18082;
      final defaultUrl = 'http://localhost:$defaultPort';
      final defaultTitle = '${spec.title} WASM Studio';
      final url = _extractStudioUrl(root, defaultUrl);
      return SizedBox.expand(
        child: _InteractiveWasmBrowserFrame(
          key: ValueKey('studio-wasm-frame-$name'),
          title: defaultTitle,
          url: url,
          executionMode: 'wasm-ui',
          height: 0,
        ),
      );
    }
    return _node(context, root, 0);
  }
}
