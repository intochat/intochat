part of 'live_desktop_shell.dart';

extension _LiveDesktopViewParser on _LiveDesktopShellState {
  String _titleForAppId(String appId) {
    for (final s in allSmartAppSpecs) {
      if (s.id == appId) return s.name;
    }
    final spec = IcAppIcon.builtInSpecs[appId.toLowerCase()];
    if (spec != null) return spec.title;
    return appId.isEmpty
        ? 'App'
        : '${appId[0].toUpperCase()}${appId.substring(1)}';
  }

  String _inferAppId(String title, Map<String, dynamic>? pinNode) {
    if (pinNode != null && pinNode['action'] is String) {
      final act = pinNode['action'] as String;
      final colon = act.indexOf(':');
      if (colon > 0 && colon + 1 < act.length) {
        return act.substring(colon + 1);
      }
    }
    final lower = title.toLowerCase();
    if (lower.contains('ino')) return 'ino';
    if (lower.contains('aspire')) return 'aspire';
    final artCraftSpec = IcAppIcon.findArtCraftByUrlOrName(lower);
    if (artCraftSpec != null) return artCraftSpec.appId;
    if (lower.contains('videocraft')) return 'filmcraft';
    if (lower.contains('gallery')) return 'gallery';
    if (lower.contains('creator')) return 'creator';
    if (lower.contains('file')) return 'files';
    if (lower.contains('data')) return 'data';
    if (lower.contains('markdraw')) return 'markdraw';
    if (lower.contains('task')) return 'tasks';
    if (lower.contains('trace')) return 'trace';
    if (lower.contains('calc')) return 'calculator';
    if (lower.contains('friend') || lower.contains('post')) return 'friends';
    if (lower.contains('workspace')) return 'workspace';
    if (lower.contains('brain')) return 'brain';
    if (lower.contains('setting')) return 'settings';
    if (lower.contains('circle')) return 'friends';
    return lower.replaceAll(RegExp(r'[^a-z0-9]'), '');
  }

  List<_ParsedDesktopWindow> _parseWindows(Map root) {
    final topChildren = (root['children'] as List? ?? const [])
        .whereType<Map>()
        .toList();

    final frameNodes = <Map>[];
    if (topChildren.isNotEmpty && topChildren.first['key'] == 'windows') {
      final windowsNode = topChildren.first;
      void collectFrames(Map node) {
        final key = node['key'] as String? ?? '';
        if (key.startsWith('frame-')) {
          frameNodes.add(node);
          return;
        }
        for (final child
            in (node['children'] as List? ?? const []).whereType<Map>()) {
          collectFrames(child);
        }
      }

      collectFrames(windowsNode);
    }

    final serverWindowKeys = <String>{};
    final serverAppIds = <String>{};
    final result = <_ParsedDesktopWindow>[];
    for (var i = 0; i < frameNodes.length; i++) {
      final frame = frameNodes[i];
      final frameKey = frame['key'] as String;
      final winId = frameKey.substring('frame-'.length);

      final numericId =
          int.tryParse(winId.replaceAll(RegExp(r'[^0-9]'), '')) ?? (i + 1);
      final children = (frame['children'] as List? ?? const [])
          .whereType<Map>()
          .toList();

      Map<String, dynamic>? focusNode;
      Map<String, dynamic>? newTabNode;
      Map<String, dynamic>? starNode;
      Map<String, dynamic>? pinNode;
      Map<String, dynamic>? closeNode;
      var title = winId;

      if (children.isNotEmpty) {
        final chromeChildren = (children[0]['children'] as List? ?? const [])
            .whereType<Map>();
        for (final btn in chromeChildren) {
          final k = btn['key'] as String? ?? '';
          final map = Map<String, dynamic>.from(btn);
          if (k.startsWith('focus-')) {
            focusNode = map;
            title = (btn['text'] as String?) ?? title;
          } else if (k.startsWith('newtab-')) {
            newTabNode = map;
          } else if (k.startsWith('star-')) {
            starNode = map;
          } else if (k.startsWith('pin-') || k.startsWith('unpin-')) {
            pinNode = map;
          } else if (k.startsWith('close-')) {
            closeNode = map;
          }
        }
      }

      final tabs = <Map<String, dynamic>>[];
      if (children.length > 1) {
        for (final t
            in (children[1]['children'] as List? ?? const [])
                .whereType<Map>()) {
          tabs.add(Map<String, dynamic>.from(t));
        }
      }

      Map<String, dynamic>? viewportRef;
      for (final c in children) {
        if (c['kind'] == 5 && c['reference'] is Map) {
          viewportRef = Map<String, dynamic>.from(c['reference'] as Map);
        }
      }
      if (viewportRef == null) continue;

      final appId = _inferAppId(title, pinNode);
      final winKey = '$winId:$appId';
      serverWindowKeys.add(winKey);
      serverAppIds.add(appId);
      if (_clientClosedWindowIds.contains(winKey) ||
          _clientClosedWindowIds.contains(winId)) {
        continue;
      }

      if (IcAppIcon.isArtCraftApp(appId) || appId == 'brain') {
        viewportRef = <String, dynamic>{
          'kind': 'view',
          'name': '${widget.brain}/apps/$appId/view',
        };
      } else if (appId == 'files' &&
          (viewportRef['name'] as String? ?? '').endsWith('/apps/files/main')) {
        viewportRef = <String, dynamic>{
          'kind': 'view',
          'name': '${widget.brain}/apps/files/view',
        };
      }
      result.add(
        _ParsedDesktopWindow(
          windowId: winId,
          numericId: numericId,
          appId: appId,
          title: title,
          neuronUri: 'neuron://${widget.brain}/apps/$appId',
          viewportRef: viewportRef,
          tabs: tabs,
          focusNode: focusNode,
          newTabNode: newTabNode,
          starNode: starNode,
          pinNode: pinNode,
          closeNode: closeNode,
        ),
      );
    }

    _clientClosedWindowIds.removeWhere(
      (k) => !k.startsWith('local-') && !serverWindowKeys.contains(k),
    );
    _clientOpenedApps.removeWhere(serverAppIds.contains);

    for (var i = 0; i < _clientOpenedApps.length; i++) {
      final appId = _clientOpenedApps[i];
      if (result.any((w) => w.appId == appId)) continue;
      final localWinId = 'local-$appId';
      if (_clientClosedWindowIds.contains('$localWinId:$appId') ||
          _clientClosedWindowIds.contains(localWinId)) {
        continue;
      }
      result.add(
        _ParsedDesktopWindow(
          windowId: localWinId,
          numericId: 100 + i,
          appId: appId,
          title: _titleForAppId(appId),
          neuronUri: 'neuron://${widget.brain}/apps/$appId',
          viewportRef: <String, dynamic>{
            'kind': 'view',
            'name': '${widget.brain}/apps/$appId/view',
          },
          tabs: const [],
        ),
      );
    }

    return result;
  }

  List<String> _parseOpenWidgets(Map root) {
    String? rawText;
    void walk(Map node) {
      if (rawText != null) return;
      if (node['key'] == 'open-widgets') {
        rawText = node['text'] as String? ?? '';
        return;
      }
      for (final child
          in (node['children'] as List? ?? const []).whereType<Map>()) {
        walk(child);
      }
    }

    walk(root);
    final serverWidgets = <String>{
      if (rawText != null && rawText!.trim().isNotEmpty)
        for (final part in rawText!.split(','))
          if (part.trim().isNotEmpty) part.trim().toLowerCase(),
    };
    _clientOpenedWidgets.removeWhere(serverWidgets.contains);
    _clientClosedWidgets.removeWhere((id) => !serverWidgets.contains(id));

    return [
      for (final id in SmartDesktopState.defaultCardOrder)
        if ((serverWidgets.contains(id) || _clientOpenedWidgets.contains(id)) &&
            !_clientClosedWidgets.contains(id))
          id,
    ];
  }

  List<WhiteboardCardData> _parseWhiteboardCards(Map root) {
    final cards = <WhiteboardCardData>[];
    final rawChildren =
        (root['children'] as List? ?? const []).whereType<Map>().toList();

    Map? whiteboardNode;
    for (final child in rawChildren) {
      if (child['key'] == 'whiteboard') {
        whiteboardNode = child;
        break;
      }
    }

    final cardNodes = whiteboardNode != null
        ? (whiteboardNode['children'] as List? ?? const []).whereType<Map>()
        : rawChildren.where((c) {
            final k = '${c['key']}';
            return k.startsWith('card-') || k.startsWith('whiteboard-');
          });

    for (final node in cardNodes) {
      final fullKey = node['key'] as String? ?? '';
      final id = fullKey.startsWith('card-')
          ? fullKey.substring(5)
          : fullKey.startsWith('whiteboard-')
              ? fullKey.substring(11)
              : fullKey;
      final title = node['text'] as String? ?? id;

      Map<String, dynamic>? meta;
      if (node['detail'] is String) {
        try {
          final decoded = jsonDecode(node['detail'] as String);
          if (decoded is Map) meta = Map<String, dynamic>.from(decoded);
        } catch (_) {}
      }

      Rect? bounds;
      if (meta != null &&
          meta['x'] is num &&
          meta['y'] is num &&
          meta['width'] is num &&
          meta['height'] is num) {
        bounds = Rect.fromLTWH(
          (meta['x'] as num).toDouble(),
          (meta['y'] as num).toDouble(),
          (meta['width'] as num).toDouble(),
          (meta['height'] as num).toDouble(),
        );
      }

      final isPinned = meta?['pinned'] == true;
      cards.add(
        WhiteboardCardData(
          id: id,
          title: title,
          content: Map<String, dynamic>.from(node),
          initialBounds: bounds,
          isPinned: isPinned,
        ),
      );
    }
    return cards;
  }

  Map<String, Map<String, dynamic>> _parseDockButtons(Map root) {
    final buttons = <String, Map<String, dynamic>>{};
    void walk(Map node) {
      final key = node['key'] as String? ?? '';
      if (node['kind'] == 3 && key.isNotEmpty) {
        buttons[key] = Map<String, dynamic>.from(node);
      } else if (node['kind'] == 1 && key.isNotEmpty) {
        buttons[key] = Map<String, dynamic>.from(node);
      }
      for (final child
          in (node['children'] as List? ?? const []).whereType<Map>()) {
        walk(child);
      }
    }

    final topChildren = (root['children'] as List? ?? const [])
        .whereType<Map>()
        .toList();
    for (final child in topChildren) {
      if (child['key'] == 'dock' || child['key'] == 'bar') walk(child);
    }
    return buttons;
  }

  IconData _iconForApp(String? name) {
    if (name != null) {
      final spec = IcAppIcon.builtInSpecs[name.toLowerCase()];
      if (spec != null) return spec.icon;
      final icon = uiIcon(name);
      if (icon != null) return icon;
    }
    return LucideIcons.layoutGrid;
  }

  List<SmartAppTileSpec> _parseAppSpecs(Map<String, Map<String, dynamic>> dockButtons) {
    if (dockButtons.isEmpty) return defaultSmartAppSpecs;
    final specs = <SmartAppTileSpec>[];
    for (final entry in dockButtons.entries) {
      final key = entry.key;
      if (!key.startsWith('dock-')) continue;
      final appId = key.substring('dock-'.length);
      if (appId == 'more-settings' ||
          appId == 'avatar' ||
          appId == 'usage' ||
          appId == 'open-widgets') {
        continue;
      }
      final btn = entry.value;
      final label = btn['text'] as String? ?? appId;
      final iconName = btn['icon'] as String? ?? appId;
      specs.add(
        SmartAppTileSpec(
          id: appId,
          name: label,
          icon: _iconForApp(iconName),
          color: const Color(0xFF3D7EFF),
          neuronUri: 'neuron://${widget.brain}/apps/$appId',
          category: 'Apps',
        ),
      );
    }
    return specs.isNotEmpty ? specs : defaultSmartAppSpecs;
  }

  List<SmartAppTileSpec> _parseCatalogSpecs(Map root) {
    final topChildren = (root['children'] as List? ?? const [])
        .whereType<Map>()
        .toList();
    Map? catalogNode;
    for (final child in topChildren) {
      if (child['key'] == 'catalog' || child['key'] == 'Launcher') {
        catalogNode = child;
        break;
      }
    }
    if (catalogNode == null) return allSmartAppSpecs;

    final byId = {for (final s in allSmartAppSpecs) s.id: s};
    final specs = <SmartAppTileSpec>[];
    void walk(Map node) {
      final key = node['key'] as String? ?? '';
      if (key.startsWith('launch-')) {
        final appId = key.substring('launch-'.length).toLowerCase();
        final builtIn = byId[appId];
        final label = node['text'] as String? ?? builtIn?.name ?? appId;
        final iconName = node['icon'] as String? ?? appId;
        final category =
            node['badge'] as String? ?? builtIn?.category ?? 'Apps';
        specs.add(
          SmartAppTileSpec(
            id: appId,
            name: label,
            icon: builtIn?.icon ?? _iconForApp(iconName),
            color: builtIn?.color ?? const Color(0xFF3D7EFF),
            accentHex: builtIn?.accentHex,
            neuronUri:
                builtIn?.neuronUri ?? 'neuron://${widget.brain}/apps/$appId',
            category: category == 'PIN'
                ? (builtIn?.category ?? 'Apps')
                : category,
          ),
        );
      }
      for (final child
          in (node['children'] as List? ?? const []).whereType<Map>()) {
        walk(child);
      }
    }

    walk(catalogNode);
    return specs.isNotEmpty ? specs : allSmartAppSpecs;
  }
}
