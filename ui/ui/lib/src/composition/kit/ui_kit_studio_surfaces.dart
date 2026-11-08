part of '../neuron_view.dart';

class _ImmutableFieldBox extends StatefulWidget {
  const _ImmutableFieldBox({
    required this.label,
    required this.value,
    required this.placeholder,
    required this.secret,
    required this.enabled,
    this.onSubmit,
    this.onSubmittedValue,
    this.onChanged,
  });

  final String label;
  final String value;
  final String placeholder;
  final bool secret;
  final bool enabled;
  final VoidCallback? onSubmit;
  final ValueChanged<String>? onSubmittedValue;
  final ValueChanged<String>? onChanged;

  @override
  State<_ImmutableFieldBox> createState() => _ImmutableFieldBoxState();
}

class _ImmutableFieldBoxState extends State<_ImmutableFieldBox> {
  late final TextEditingController _controller = TextEditingController(
    text: widget.value,
  );
  final FocusNode _focus = FocusNode();

  @override
  void didUpdateWidget(_ImmutableFieldBox oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.value != widget.value && _controller.text != widget.value) {
      _controller.text = widget.value;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => UiKitField(
    label: widget.label,
    placeholder: widget.placeholder,
    controller: _controller,
    focusNode: _focus,
    secret: widget.secret,
    enabled: widget.enabled,
    onChanged: widget.onChanged,
    onSubmitted: widget.onSubmittedValue != null
        ? (val) => widget.onSubmittedValue!(val)
        : widget.onSubmit != null
        ? (_) => widget.onSubmit!()
        : null,
  );
}

class _StudioNavRailItem extends StatefulWidget {
  const _StudioNavRailItem({
    required this.label,
    required this.selected,
    this.icon,
    this.badge,
    this.onTap,
  });

  final String label;
  final IconData? icon;
  final bool selected;
  final String? badge;
  final VoidCallback? onTap;

  @override
  State<_StudioNavRailItem> createState() => _StudioNavRailItemState();
}

class _StudioNavRailItemState extends State<_StudioNavRailItem> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return MouseRegion(
      cursor: widget.onTap != null
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 110),
          height: 32,
          padding: const EdgeInsets.symmetric(horizontal: 10),
          decoration: BoxDecoration(
            color: widget.selected
                ? c.accentTint
                : (_hovered ? c.surfaceCard : Colors.transparent),
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(
              color: widget.selected
                  ? c.accent.withValues(alpha: 0.35)
                  : Colors.transparent,
            ),
          ),
          child: Row(
            children: [
              if (widget.icon != null) ...[
                Icon(
                  widget.icon,
                  size: 14,
                  color: widget.selected ? c.accent : c.textMuted,
                ),
                const SizedBox(width: 8),
              ],
              Expanded(
                child: Text(
                  widget.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    12.5,
                    weight: widget.selected ? FontWeight.w600 : FontWeight.w500,
                    color: widget.selected ? c.textStrong : c.textBody,
                  ),
                ),
              ),
              if (widget.badge != null && widget.badge!.isNotEmpty)
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 6,
                    vertical: 1.5,
                  ),
                  decoration: BoxDecoration(
                    color: widget.selected ? c.surfaceCard : c.surfaceSunk,
                    borderRadius: BorderRadius.circular(999),
                    border: Border.all(color: c.borderHairline),
                  ),
                  child: Text(
                    widget.badge!,
                    style: IcType.text(
                      10,
                      weight: FontWeight.w600,
                      color: widget.selected ? c.accent : c.textMuted,
                      tnum: true,
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _InteractiveBrainSceneBox extends StatefulWidget {
  const _InteractiveBrainSceneBox({
    required this.height,
    required this.nodes,
    required this.synapses,
    this.initialSelectedId,
    this.pauseRotation = false,
    this.zoomTargetId,
    this.targetZoom = 1.65,
    required this.interactive,
    required this.showHud,
    required this.onSelectNodeIndex,
  });

  final double height;
  final List<BrainNode3D> nodes;
  final List<BrainSynapse3D> synapses;
  final String? initialSelectedId;
  final bool pauseRotation;
  final String? zoomTargetId;
  final double targetZoom;
  final bool interactive;
  final bool showHud;
  final ValueChanged<int> onSelectNodeIndex;

  @override
  State<_InteractiveBrainSceneBox> createState() =>
      _InteractiveBrainSceneBoxState();
}

class _InteractiveBrainSceneBoxState extends State<_InteractiveBrainSceneBox> {
  late String? _selectedId =
      widget.zoomTargetId ??
      widget.initialSelectedId ??
      (widget.nodes.isNotEmpty ? widget.nodes.first.id : null);
  bool _showInspector = false;
  late bool _nodeSelectedOrPaused =
      widget.pauseRotation ||
      widget.zoomTargetId != null ||
      (widget.initialSelectedId != null &&
          widget.initialSelectedId!.isNotEmpty);

  @override
  void didUpdateWidget(_InteractiveBrainSceneBox oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.zoomTargetId != widget.zoomTargetId &&
        widget.zoomTargetId != null) {
      _selectedId = widget.zoomTargetId;
      _nodeSelectedOrPaused = true;
    } else if (oldWidget.initialSelectedId != widget.initialSelectedId &&
        widget.initialSelectedId != null) {
      _selectedId = widget.initialSelectedId;
      _nodeSelectedOrPaused = true;
    }
    if (widget.pauseRotation && !_nodeSelectedOrPaused) {
      _nodeSelectedOrPaused = true;
    }
  }

  void _selectNode(BrainNode3D node) {
    setState(() {
      _selectedId = node.id;
      _nodeSelectedOrPaused = true;
    });
    final idx = widget.nodes.indexWhere((item) => item.id == node.id);
    if (idx >= 0) widget.onSelectNodeIndex(idx);
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final selectedNode = widget.nodes.firstWhere(
      (n) => n.id == _selectedId,
      orElse: () => widget.nodes.first,
    );
    final effectivePause = widget.pauseRotation || _nodeSelectedOrPaused;
    final effectiveZoomTarget = _nodeSelectedOrPaused
        ? (_selectedId ?? widget.zoomTargetId)
        : widget.zoomTargetId;

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SizedBox(
          height: widget.height,
          width: double.infinity,
          child: ClipRRect(
            borderRadius: BorderRadius.circular(IcRadius.card),
            child: BrainScene3D(
              nodes: widget.nodes,
              synapses: widget.synapses,
              selectedId: _selectedId,
              pauseRotation: effectivePause,
              zoomTargetId: effectiveZoomTarget,
              targetZoom: widget.targetZoom,
              interactive: widget.interactive,
              showHud: widget.showHud,
              onSelectNode: _selectNode,
            ),
          ),
        ),
        const SizedBox(height: 10),
        IcAcrylicBox(
          padding: const EdgeInsets.all(12),
          borderRadius: BorderRadius.circular(IcRadius.card),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  IcAppIcon(
                    appId: selectedNode.appId ?? selectedNode.id,
                    size: 24,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          selectedNode.label,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.text(
                            13.5,
                            weight: FontWeight.w700,
                            color: c.textStrong,
                          ),
                        ),
                        Text(
                          selectedNode.neuronUri,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.code(11.5, color: c.textMuted),
                        ),
                      ],
                    ),
                  ),
                  TagPill(label: selectedNode.moduleId, tone: TagTone.neutral),
                  const SizedBox(width: 6),
                  TagPill(
                    label: '${selectedNode.signalsPerMin} signals/min',
                    tone: TagTone.accent,
                    icon: LucideIcons.zap,
                  ),
                ],
              ),
              const SizedBox(height: 8),
              Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  Btn(
                    key: ValueKey('brain3d-inspect-state-${selectedNode.id}'),
                    label: _showInspector ? 'Hide State' : 'Inspect State',
                    small: true,
                    primary: _showInspector,
                    icon: LucideIcons.scanLine,
                    onTap: () => setState(() => _showInspector = !_showInspector),
                  ),
                  Btn(
                    key: ValueKey('brain3d-copy-uri-${selectedNode.id}'),
                    label: 'Copy URI',
                    small: true,
                    icon: LucideIcons.copy,
                    onTap: () {
                      Clipboard.setData(
                        ClipboardData(text: selectedNode.neuronUri),
                      );
                      showKitToast(context, 'Copied ${selectedNode.neuronUri}');
                    },
                  ),
                ],
              ),
              if (selectedNode.subtitle != null &&
                  selectedNode.subtitle!.isNotEmpty &&
                  selectedNode.subtitle != selectedNode.neuronUri) ...[
                const SizedBox(height: 6),
                Text(
                  selectedNode.subtitle!,
                  style: IcType.text(12, color: c.textBody),
                ),
              ],
              if (_showInspector) ...[
                const SizedBox(height: 10),
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(
                    color: c.surfaceCard,
                    borderRadius: BorderRadius.circular(IcRadius.sm),
                    border: Border.all(color: c.borderHairline),
                  ),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Icon(LucideIcons.terminal, size: 14, color: c.accent),
                          const SizedBox(width: 6),
                          Text(
                            'LIVE NEURON STATE',
                            style: IcType.text(
                              11,
                              weight: FontWeight.w700,
                              color: c.accent,
                            ),
                          ),
                          const Spacer(),
                          const TagPill(
                            label: 'IInspectableNeuron',
                            tone: TagTone.accent,
                          ),
                        ],
                      ),
                      const SizedBox(height: 8),
                      Container(
                        width: double.infinity,
                        padding: const EdgeInsets.all(8),
                        decoration: BoxDecoration(
                          color: c.surfaceSunk,
                          borderRadius: BorderRadius.circular(IcRadius.sm),
                        ),
                        child: SelectableText(
                          '{\n  "neuron": "${selectedNode.neuronUri}",\n  "type": "${selectedNode.moduleId}",\n  "status": "active",\n  "signalsPerMin": ${selectedNode.signalsPerMin}\n}',
                          style: IcType.code(11.5, color: c.textStrong),
                        ),
                      ),
                    ],
                  ),
                ),
              ],
              if (widget.nodes.length > 1) ...[
                const SizedBox(height: 8),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final n in widget.nodes)
                      Btn(
                        key: ValueKey('brain3d-select-${n.id}'),
                        label: n.label,
                        small: true,
                        primary: n.id == selectedNode.id,
                        onTap: () => _selectNode(n),
                      ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _InteractiveWasmBrowserFrame extends StatefulWidget {
  const _InteractiveWasmBrowserFrame({
    super.key,
    required this.title,
    required this.url,
    required this.executionMode,
    required this.height,
    this.subtitle,
    this.onReload,
    this.children = const [],
  });

  final String title;
  final String url;
  final String executionMode;
  final double height;
  final String? subtitle;
  final VoidCallback? onReload;
  final List<Widget> children;

  @override
  State<_InteractiveWasmBrowserFrame> createState() =>
      _InteractiveWasmBrowserFrameState();
}

class _InteractiveWasmBrowserFrameState
    extends State<_InteractiveWasmBrowserFrame> {
  int _reloadSession = 0;

  String get _appId {
    final spec = IcAppIcon.findArtCraftByUrlOrName(
      '${widget.title} ${widget.url}',
    );
    return spec?.appId ?? 'photocraft';
  }

  String get _resolvedUrl {
    final raw = widget.url.trim();
    if (kIsWeb) {
      final base = Uri.base;
      final isLocalOrEmpty =
          raw.isEmpty ||
          raw.startsWith('/artcraft/') ||
          raw.contains('localhost') ||
          raw.contains('127.0.0.1');
      if (isLocalOrEmpty && (base.scheme == 'http' || base.scheme == 'https')) {
        final uiBase = IntoChatOSHostEnv.resolveUiBaseRaw();
        if (uiBase.isNotEmpty) {
          final parsed = Uri.tryParse(uiBase);
          if (parsed != null && parsed.hasAuthority) {
            return '${parsed.origin}/artcraft/$_appId/index.html';
          }
        }
        return '${base.origin}/artcraft/$_appId/index.html';
      }
    }
    return raw;
  }

  String get _formattedMode {
    final m = widget.executionMode.toLowerCase();
    if (m.contains('wasm')) return 'WASM Web UI';
    if (m.contains('mcp')) return 'MCP Server';
    if (m.contains('native')) return 'Native Linux CLI';
    return widget.executionMode;
  }

  void _triggerReload() {
    setState(() => _reloadSession++);
    widget.onReload?.call();
    if (mounted) {
      showKitToast(context, 'Reloaded ${widget.title} (${widget.url})');
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final resolvedUrl = _resolvedUrl;
    return LayoutBuilder(
      builder: (context, constraints) {
        final boundedHeight = constraints.hasBoundedHeight;
        final fallbackHeight = widget.height > 0 ? widget.height : 520.0;
        final browserViewport = Container(
          key: ValueKey('webbrowser-viewport-$_reloadSession'),
          height: boundedHeight ? null : fallbackHeight,
          width: double.infinity,
          clipBehavior: Clip.antiAlias,
          decoration: BoxDecoration(
            color: c.surfaceSunk,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.borderInput),
          ),
          child: EmbeddedBrowser(
            key: ValueKey('embedded-wasm-$resolvedUrl-$_reloadSession'),
            uri: resolvedUrl,
            onConnect: null,
            onDisconnect: null,
          ),
        );

        return IcAcrylicBox(
          key: ValueKey('wasm-browser-frame-${widget.url}'),
          padding: const EdgeInsets.all(8),
          borderRadius: BorderRadius.circular(IcRadius.card),
          child: Column(
            mainAxisSize: boundedHeight ? MainAxisSize.max : MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  IcAppIcon(appId: _appId, size: 20),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      widget.title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        13,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                  GestureDetector(
                    onTap: () {
                      Clipboard.setData(ClipboardData(text: widget.url));
                      showKitToast(context, 'Copied ${widget.url}');
                    },
                    child: MouseRegion(
                      cursor: SystemMouseCursors.click,
                      child: TagPill(
                        key: const ValueKey('webbrowser-url-pill'),
                        label: widget.url,
                        tone: TagTone.neutral,
                        icon: LucideIcons.globe,
                      ),
                    ),
                  ),
                  const SizedBox(width: 6),
                  TagPill(
                    key: const ValueKey('webbrowser-mode-badge'),
                    label: _formattedMode,
                    tone: TagTone.accent,
                    icon: LucideIcons.cpu,
                  ),
                  const SizedBox(width: 6),
                  TagPill(
                    label: 'Session #${_reloadSession + 1}',
                    tone: TagTone.done,
                    icon: LucideIcons.circleCheck,
                  ),
                  const SizedBox(width: 6),
                  Tooltip(
                    message: 'Reload WASM Studio',
                    child: GestureDetector(
                      key: const ValueKey('webbrowser-reload-btn'),
                      onTap: _triggerReload,
                      child: MouseRegion(
                        cursor: SystemMouseCursors.click,
                        child: Container(
                          width: 26,
                          height: 26,
                          decoration: BoxDecoration(
                            color: c.surfaceSunk,
                            borderRadius: BorderRadius.circular(IcRadius.xs),
                            border: Border.all(color: c.borderHairline),
                          ),
                          child: Icon(
                            LucideIcons.rotateCw,
                            size: 13,
                            color: c.textStrong,
                          ),
                        ),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 6),
              if (boundedHeight)
                Expanded(child: browserViewport)
              else
                browserViewport,
            ],
          ),
        );
      },
    );
  }
}

class _InteractiveMarkdrawStudioCard extends StatefulWidget {
  const _InteractiveMarkdrawStudioCard({
    super.key,
    required this.title,
    required this.activeDocument,
    required this.format,
    required this.content,
    required this.revision,
    required this.status,
    this.lastExportPath,
    required this.height,
    required this.onSyncScene,
    required this.onExport,
  });

  final String title;
  final String activeDocument;
  final String format;
  final String content;
  final int revision;
  final String status;
  final String? lastExportPath;
  final double height;
  final void Function(String serializedContent, String documentPath)
  onSyncScene;
  final void Function(String format) onExport;

  @override
  State<_InteractiveMarkdrawStudioCard> createState() =>
      _InteractiveMarkdrawStudioCardState();
}

class _InteractiveMarkdrawStudioCardState
    extends State<_InteractiveMarkdrawStudioCard> {
  late final md.MarkdrawController _controller = md.MarkdrawController(
    config: const md.MarkdrawEditorConfig(initialBackground: '#0f172a'),
  );
  String _lastLoadedContent = '';
  int _lastLoadedRevision = -1;

  @override
  void initState() {
    super.initState();
    _loadScene(widget.content, widget.activeDocument, widget.revision);
  }

  @override
  void didUpdateWidget(_InteractiveMarkdrawStudioCard oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.revision != _lastLoadedRevision ||
        widget.activeDocument != oldWidget.activeDocument ||
        (widget.content != oldWidget.content &&
            widget.content != _lastLoadedContent)) {
      _loadScene(widget.content, widget.activeDocument, widget.revision);
    }
  }

  void _loadScene(String rawContent, String docPath, int rev) {
    if (rawContent.trim().isEmpty) return;
    try {
      final fileName = docPath.endsWith('.excalidraw')
          ? docPath
          : '$docPath.markdraw';
      _controller.loadFromContent(rawContent, fileName);
      _lastLoadedContent = rawContent;
      _lastLoadedRevision = rev;
    } catch (_) {
      // Ignore transient parse errors if partial markdown is being streamed
    }
  }

  void _syncLiveCanvasToBackend() {
    try {
      final docFormat = widget.activeDocument.endsWith('.excalidraw')
          ? md.DocumentFormat.excalidraw
          : md.DocumentFormat.markdraw;
      final serialized = _controller.serializeScene(format: docFormat);
      _lastLoadedContent = serialized;
      widget.onSyncScene(serialized, widget.activeDocument);
      if (mounted) {
        showKitToast(
          context,
          'Synced Markdraw canvas to ${widget.activeDocument}',
        );
      }
    } catch (_) {}
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return IcAcrylicBox(
      padding: const EdgeInsets.all(12),
      borderRadius: BorderRadius.circular(IcRadius.card),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              const IcAppIcon(appId: 'markdraw', size: 24),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      widget.title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        13,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                    Text(
                      '${widget.activeDocument} · rev ${widget.revision}${widget.lastExportPath != null ? ' · exported ${widget.lastExportPath}' : ''}',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.code(11, color: c.textMuted),
                    ),
                  ],
                ),
              ),
              Btn(
                label: 'Sync Canvas',
                small: true,
                primary: true,
                icon: LucideIcons.refreshCw,
                onTap: _syncLiveCanvasToBackend,
              ),
              const SizedBox(width: 6),
              Btn(
                label: '.excalidraw',
                small: true,
                icon: LucideIcons.fileCode,
                onTap: () {
                  final json = _controller.serializeScene(
                    format: md.DocumentFormat.excalidraw,
                  );
                  Clipboard.setData(ClipboardData(text: json));
                  widget.onExport('excalidraw');
                  showKitToast(
                    context,
                    'Exported .excalidraw to Files & copied JSON',
                  );
                },
              ),
              const SizedBox(width: 6),
              Btn(
                label: '.svg',
                small: true,
                icon: LucideIcons.image,
                onTap: () {
                  final svg = _controller.exportSvg(selectedOnly: false);
                  Clipboard.setData(ClipboardData(text: svg));
                  widget.onExport('svg');
                  showKitToast(context, 'Exported .svg to Files & copied SVG');
                },
              ),
            ],
          ),
          const SizedBox(height: 10),
          SizedBox(
            height: widget.height,
            width: double.infinity,
            child: ClipRRect(
              borderRadius: BorderRadius.circular(IcRadius.sm),
              child: md.MarkdrawEditor(
                controller: _controller,
                onSave: _syncLiveCanvasToBackend,
                onExportSvg: () => widget.onExport('svg'),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
