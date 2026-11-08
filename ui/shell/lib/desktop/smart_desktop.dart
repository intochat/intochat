import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

export 'app_catalog_launcher.dart';
import 'app_catalog_launcher.dart';

/// Interactive Spatial Workspace Home Screen over a static wallpaper (`IcDesktopWallpaper`)
/// with draggable, resizable sticky-note Smart Cards, Pinning, and Auto-Arrange.
/// (The 3D neural graph now lives exclusively inside the dedicated `Brain` app.)
class SmartDesktop extends StatefulWidget {
  const SmartDesktop({
    super.key,
    this.hasOpenWindows = false,
    this.stylePreset = IcStylePreset.fluentAcrylic,
    this.barLayout = BarLayout.floatingGlass,
    this.tokensToday = 14800,
    this.apps = defaultSmartAppSpecs,
    this.onLaunchApp,
    this.onOpenAllApps,
    this.onOpenPalette,
    this.onApproveSignal,
    this.onSelectPreset,
    this.onCycleBarLayout,
    this.onToggleDark,
    this.onAutoArrange,
    this.onToast,
    this.openCardIds,
    this.onOpenCard,
    this.onCloseCard,
    this.widgetsMenuOpen,
    this.onWidgetsMenuChanged,
    this.dynamicCards,
    this.cardContentBuilder,
  });

  final bool hasOpenWindows;
  final IcStylePreset stylePreset;
  final BarLayout barLayout;
  final int tokensToday;
  final List<SmartAppTileSpec> apps;
  final ValueChanged<String>? onLaunchApp;
  final VoidCallback? onOpenAllApps;
  final VoidCallback? onOpenPalette;
  final VoidCallback? onApproveSignal;
  final ValueChanged<IcStylePreset>? onSelectPreset;
  final VoidCallback? onCycleBarLayout;
  final VoidCallback? onToggleDark;
  final VoidCallback? onAutoArrange;
  final ValueChanged<String>? onToast;
  final List<String>? openCardIds;
  final ValueChanged<String>? onOpenCard;
  final ValueChanged<String>? onCloseCard;
  final bool? widgetsMenuOpen;
  final ValueChanged<bool>? onWidgetsMenuChanged;
  final List<WhiteboardCardData>? dynamicCards;
  final Widget Function(Map<String, dynamic> content)? cardContentBuilder;

  @override
  State<SmartDesktop> createState() => SmartDesktopState();
}

class SmartDesktopState extends State<SmartDesktop> {
  static const List<String> defaultCardOrder = [
    'launcher',
    'signal',
    'identity',
    'apps',
  ];

  static const Map<String, String> defaultCardLabels = {
    'launcher': 'Ino Launcher',
    'signal': 'Live Signal',
    'identity': 'Theme & Identity',
    'apps': 'Smart Apps',
  };

  List<String> get cardOrder =>
      widget.dynamicCards != null && widget.dynamicCards!.isNotEmpty
          ? widget.dynamicCards!.map((c) => c.id).toList()
          : defaultCardOrder;

  Map<String, String> get cardLabels {
    if (widget.dynamicCards != null && widget.dynamicCards!.isNotEmpty) {
      return {for (final c in widget.dynamicCards!) c.id: c.title};
    }
    return defaultCardLabels;
  }

  bool _briefApproved = false;
  bool _initializedBounds = false;
  bool _widgetsMenuOpen = false;
  Offset _workspacePanOffset = Offset.zero;
  double _workspaceZoom = 1.0;

  bool get isWidgetsMenuOpen => widget.widgetsMenuOpen ?? _widgetsMenuOpen;
  Offset get workspacePanOffset => _workspacePanOffset;
  double get workspaceZoom => _workspaceZoom;

  void resetWorkspaceCamera() {
    setState(() {
      _workspacePanOffset = Offset.zero;
      _workspaceZoom = 1.0;
    });
  }

  final Map<String, Rect> _cardBounds = {};
  final Set<String> _pinnedCardIds = {};
  final Set<String> _localClosedCardIds = {};
  final Set<String> _localOpenedCardIds = {};

  @override
  void initState() {
    super.initState();
    if (widget.dynamicCards != null) {
      for (final card in widget.dynamicCards!) {
        if (card.isPinned) {
          _pinnedCardIds.add(card.id);
        }
        if (card.initialBounds != null) {
          _cardBounds[card.id] = card.initialBounds!;
        }
      }
    }
  }

  @override
  void didUpdateWidget(SmartDesktop oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.dynamicCards != null &&
        widget.dynamicCards != oldWidget.dynamicCards) {
      for (final card in widget.dynamicCards!) {
        if (card.isPinned && !_pinnedCardIds.contains(card.id)) {
          _pinnedCardIds.add(card.id);
        }
        if (card.initialBounds != null && !_cardBounds.containsKey(card.id)) {
          _cardBounds[card.id] = card.initialBounds!;
        }
      }
    }
  }

  Map<String, Rect> get cardBounds => Map.unmodifiable(_cardBounds);
  Set<String> get pinnedCardIds => Set.unmodifiable(_pinnedCardIds);

  List<String> get visibleCardIds {
    if (widget.openCardIds != null) {
      return [
        for (final id in cardOrder)
          if (widget.openCardIds!.contains(id)) id,
      ];
    }
    return [
      for (final id in cardOrder)
        if (!_localClosedCardIds.contains(id) ||
            _localOpenedCardIds.contains(id))
          id,
    ];
  }

  bool isCardPinned(String cardId) => _pinnedCardIds.contains(cardId);

  Rect? boundsForCard(String cardId) => _cardBounds[cardId];

  void closeCard(String cardId) {
    setState(() {
      _localClosedCardIds.add(cardId);
      _localOpenedCardIds.remove(cardId);
    });
    widget.onCloseCard?.call(cardId);
  }

  void openCard(String cardId) {
    setState(() {
      _localClosedCardIds.remove(cardId);
      _localOpenedCardIds.add(cardId);
    });
    widget.onOpenCard?.call(cardId);
  }

  void toggleCard(String cardId) {
    if (visibleCardIds.contains(cardId)) {
      closeCard(cardId);
    } else {
      openCard(cardId);
    }
  }

  void togglePinCard(String cardId) {
    setState(() {
      if (_pinnedCardIds.contains(cardId)) {
        _pinnedCardIds.remove(cardId);
      } else {
        _pinnedCardIds.add(cardId);
      }
    });
  }

  void _ensureInitialCardBounds(Size canvas) {
    if (_initializedBounds && _cardBounds.length >= cardOrder.length) return;
    _initializedBounds = true;
    final rightX = math.max(404.0, canvas.width - 364.0);

    if (widget.dynamicCards != null) {
      for (var i = 0; i < widget.dynamicCards!.length; i++) {
        final card = widget.dynamicCards![i];
        if (card.initialBounds != null) {
          _cardBounds.putIfAbsent(card.id, () => card.initialBounds!);
        } else {
          final isLeft = i % 2 == 0;
          final x = isLeft ? 24.0 : rightX;
          final y = 58.0 + (i ~/ 2) * 240.0;
          _cardBounds.putIfAbsent(card.id, () => Rect.fromLTWH(x, y, 360, 224));
        }
      }
    }

    _cardBounds.putIfAbsent(
      'launcher',
      () => const Rect.fromLTWH(24, 58, 360, 224),
    );
    _cardBounds.putIfAbsent(
      'signal',
      () => const Rect.fromLTWH(24, 296, 360, 224),
    );
    _cardBounds.putIfAbsent(
      'identity',
      () => Rect.fromLTWH(rightX, 58, 340, 276),
    );
    _cardBounds.putIfAbsent('apps', () => Rect.fromLTWH(rightX, 348, 340, 252));
  }

  void _moveCard(String id, Offset delta, Size canvas) {
    final current = _cardBounds[id];
    if (current == null) return;
    final maxLeft = math.max(12.0, canvas.width - current.width - 12.0);
    final maxTop = math.max(48.0, canvas.height - current.height - 12.0);
    setState(() {
      _cardBounds[id] = Rect.fromLTWH(
        (current.left + delta.dx).clamp(12.0, maxLeft),
        (current.top + delta.dy).clamp(48.0, maxTop),
        current.width,
        current.height,
      );
    });
  }

  void _resizeCard(String id, Offset delta, Size canvas) {
    final current = _cardBounds[id];
    if (current == null) return;
    final nextWidth = (current.width + delta.dx)
        .clamp(240.0, math.max(360.0, canvas.width - current.left - 12.0))
        .toDouble();
    final nextHeight = (current.height + delta.dy)
        .clamp(150.0, math.max(220.0, canvas.height - current.top - 12.0))
        .toDouble();
    setState(() {
      _cardBounds[id] = Rect.fromLTWH(
        current.left,
        current.top,
        nextWidth,
        nextHeight,
      );
    });
  }

  /// Neatly arranges all unpinned sticky-note cards into a clean grid while
  /// strictly locking the position (`Rect`) of any pinned card (`isPinned == true`).
  void autoArrangeWhiteboard(Size canvas) {
    _ensureInitialCardBounds(canvas);
    const startX = 24.0;
    const startY = 58.0;
    const colGap = 18.0;
    const rowGap = 16.0;
    const slotW = 356.0;
    const slotH = 248.0;

    final cols = math.max(
      1,
      ((canvas.width - startX * 2 + colGap) / (slotW + colGap)).floor(),
    );

    final lockedRects = <Rect>[
      for (final id in _pinnedCardIds)
        if (_cardBounds[id] != null) _cardBounds[id]!,
    ];

    setState(() {
      var slotIndex = 0;
      for (final id in cardOrder) {
        if (_pinnedCardIds.contains(id)) {
          // Pinned cards strictly keep their exact Rect.
          continue;
        }
        final current =
            _cardBounds[id] ?? const Rect.fromLTWH(24, 58, 356, 240);
        Rect candidate;
        while (true) {
          final col = slotIndex % cols;
          final row = slotIndex ~/ cols;
          final x = startX + col * (slotW + colGap);
          final y = startY + row * (slotH + rowGap);
          candidate = Rect.fromLTWH(x, y, current.width, current.height);
          slotIndex++;
          final overlapsPinned = lockedRects.any(
            (pinned) => pinned.inflate(6).overlaps(candidate),
          );
          if (!overlapsPinned || slotIndex > 32) break;
        }
        _cardBounds[id] = candidate;
      }
    });
    widget.onAutoArrange?.call();
    _toast('Auto-arranged unpinned whiteboard cards');
  }

  void _launch(String appId) {
    widget.onLaunchApp?.call(appId);
  }

  void _toast(String msg) {
    if (widget.onToast != null) {
      widget.onToast!(msg);
    } else if (mounted) {
      showKitToast(context, msg);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final hasOpenWindows = widget.hasOpenWindows;

    return LayoutBuilder(
      builder: (context, constraints) {
        final canvas = Size(constraints.maxWidth, constraints.maxHeight);
        _ensureInitialCardBounds(canvas);

        return Stack(
          clipBehavior: Clip.none,
          children: [
            // 1. Static Desktop Wallpaper + Spatial Workspace Surface (no 3D BrainScene on home screen)
            Positioned.fill(
              child: GestureDetector(
                key: const ValueKey('desktop-workspace-surface'),
                behavior: HitTestBehavior.translucent,
                onPanUpdate: (d) {
                  setState(() {
                    _workspacePanOffset += d.delta;
                  });
                },
                child: IcDesktopWallpaper(
                  key: const ValueKey('desktop-static-wallpaper'),
                  stylePreset: widget.stylePreset,
                  panOffset: _workspacePanOffset,
                  zoom: _workspaceZoom,
                  dimmed: hasOpenWindows,
                ),
              ),
            ),

            // 1b. Workspace Canvas Sector Bar (Top-Left)
            Positioned(
              top: 12,
              left: 24,
              child: IcAcrylicBox(
                key: const ValueKey('workspace-home-status-pill'),
                padding: const EdgeInsets.symmetric(
                  horizontal: 10,
                  vertical: 6,
                ),
                borderRadius: BorderRadius.circular(IcRadius.pill),
                shadows: IcShadow.md(c),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(LucideIcons.compass, size: 14, color: c.accent),
                    const SizedBox(width: 6),
                    Text(
                      'Spatial Workspace',
                      style: IcType.text(
                        11.5,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                    const SizedBox(width: 8),
                    TagPill(
                      label: '${allSmartAppSpecs.length} apps',
                      tone: TagTone.accent,
                      icon: LucideIcons.layoutGrid,
                    ),
                    if (_workspacePanOffset != Offset.zero) ...[
                      const SizedBox(width: 6),
                      Btn(
                        key: const ValueKey('workspace-home-reset-pan'),
                        label: 'Center',
                        small: true,
                        icon: LucideIcons.locateFixed,
                        onTap: resetWorkspaceCamera,
                      ),
                    ],
                  ],
                ),
              ),
            ),

            // 2. Whiteboard Desk Toolbar (All Apps + Widgets + Auto-Arrange + Pin Status)
            Positioned(
              top: 12,
              right: 24,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                mainAxisSize: MainAxisSize.min,
                children: [
                  IcAcrylicBox(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 10,
                      vertical: 6,
                    ),
                    borderRadius: BorderRadius.circular(IcRadius.pill),
                    shadows: IcShadow.md(c),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (widget.onOpenAllApps != null) ...[
                          Btn(
                            key: const ValueKey('whiteboard-all-apps-btn'),
                            label: 'All Apps (${allSmartAppSpecs.length})',
                            small: true,
                            primary: true,
                            icon: LucideIcons.layoutGrid,
                            onTap: widget.onOpenAllApps,
                          ),
                          const SizedBox(width: 8),
                        ],
                        TagPill(
                          key: const ValueKey('whiteboard-pinned-pill'),
                          label: '${_pinnedCardIds.length} pinned',
                          tone: _pinnedCardIds.isNotEmpty
                              ? TagTone.done
                              : TagTone.neutral,
                          icon: _pinnedCardIds.isNotEmpty
                              ? LucideIcons.pin
                              : LucideIcons.pinOff,
                        ),
                        const SizedBox(width: 8),
                        Btn(
                          key: const ValueKey('whiteboard-widgets-btn'),
                          label: 'Widgets (${visibleCardIds.length})',
                          small: true,
                          icon: LucideIcons.stickyNote,
                          onTap: () {
                            final next = !isWidgetsMenuOpen;
                            setState(() => _widgetsMenuOpen = next);
                            widget.onWidgetsMenuChanged?.call(next);
                          },
                        ),
                        const SizedBox(width: 8),
                        Btn(
                          key: const ValueKey('whiteboard-auto-arrange-btn'),
                          label: 'Auto-Arrange',
                          small: true,
                          icon: LucideIcons.layoutGrid,
                          onTap: () => autoArrangeWhiteboard(canvas),
                        ),
                      ],
                    ),
                  ),
                  if (isWidgetsMenuOpen) ...[
                    const SizedBox(height: 8),
                    IcAcrylicBox(
                      key: const ValueKey('whiteboard-widgets-menu'),
                      padding: const EdgeInsets.all(10),
                      borderRadius: BorderRadius.circular(IcRadius.card),
                      shadows: IcShadow.lg(c),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          for (final id in cardOrder)
                            Padding(
                              padding: const EdgeInsets.symmetric(vertical: 3),
                              child: Btn(
                                key: ValueKey('whiteboard-widget-toggle-$id'),
                                label: cardLabels[id] ?? id,
                                small: true,
                                primary: visibleCardIds.contains(id),
                                icon: visibleCardIds.contains(id)
                                    ? LucideIcons.check
                                    : LucideIcons.plus,
                                onTap: () => toggleCard(id),
                              ),
                            ),
                        ],
                      ),
                    ),
                  ],
                ],
              ),
            ),

            // 3. Resizable, Draggable Sticky-Note Smart Cards Layer
            for (final id in visibleCardIds)
              Positioned.fromRect(
                key: ValueKey('sticky-card-pos-$id'),
                rect: _cardBounds[id]!,
                child: AnimatedOpacity(
                  duration: IcMotion.base,
                  opacity: hasOpenWindows ? 0.88 : 1.0,
                  child: _StickyWhiteboardCard(
                    cardId: id,
                    pinned: _pinnedCardIds.contains(id),
                    onTogglePin: () => togglePinCard(id),
                    onClose: () => closeCard(id),
                    onMove: (delta) => _moveCard(id, delta, canvas),
                    onResize: (delta) => _resizeCard(id, delta, canvas),
                    child: () {
                      final dynamicCard = widget.dynamicCards
                          ?.where((c) => c.id == id)
                          .firstOrNull;
                      if (dynamicCard?.content != null &&
                          widget.cardContentBuilder != null) {
                        return widget.cardContentBuilder!(
                            dynamicCard!.content!);
                      }
                      return switch (id) {
                        'launcher' => _buildLauncherCardContent(c),
                        'signal' => _buildSignalApprovalCardContent(c),
                        'identity' => _buildIdentitySwitcherCardContent(c),
                        'apps' => _buildSmartAppGridCardContent(c),
                        _ => const SizedBox.shrink(),
                      };
                    }(),
                  ),
                ),
              ),
          ],
        );
      },
    );
  }

  Widget _buildLauncherCardContent(IcColors c) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Row(
          children: [
            const IcAppIcon(appId: 'ino', size: 32),
            const SizedBox(width: 10),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Ino · DigitalBrain OS',
                    style: IcType.text(
                      14,
                      weight: FontWeight.w700,
                      color: c.textStrong,
                    ),
                  ),
                  Text(
                    'neuron://local/apps/ino',
                    style: IcType.text(11.5, color: c.textMuted, tnum: true),
                  ),
                ],
              ),
            ),
            const TagPill(
              label: 'Online',
              tone: TagTone.done,
              icon: LucideIcons.circleCheck,
            ),
          ],
        ),
        const SizedBox(height: 14),
        GestureDetector(
          onTap: widget.onOpenPalette,
          child: MouseRegion(
            cursor: SystemMouseCursors.click,
            child: Container(
              height: 38,
              padding: const EdgeInsets.symmetric(horizontal: 12),
              decoration: BoxDecoration(
                color: c.surfaceSunk,
                borderRadius: BorderRadius.circular(IcRadius.button),
                border: Border.all(color: c.borderInput),
              ),
              child: Row(
                children: [
                  Icon(LucideIcons.search, size: 15, color: c.textMuted),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      'Ask Ino or create a smart app…',
                      style: IcType.text(13, color: c.textMuted),
                    ),
                  ),
                  const TagPill(label: '/ or ⌘K', tone: TagTone.neutral),
                ],
              ),
            ),
          ),
        ),
        const SizedBox(height: 12),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            Btn(
              label: 'UI Gallery',
              small: true,
              primary: true,
              icon: LucideIcons.layoutTemplate,
              onTap: () => _launch('gallery'),
            ),
            Btn(
              key: const ValueKey('launcher-open-brain-btn'),
              label: '3D Brain Graph',
              small: true,
              icon: LucideIcons.brainCircuit,
              onTap: () => _launch('brain'),
            ),
            Btn(
              label: '3D Creator',
              small: true,
              icon: LucideIcons.wandSparkles,
              onTap: () => _launch('creator'),
            ),
            Btn(
              label: 'Open Ino',
              small: true,
              icon: LucideIcons.messagesSquare,
              onTap: () => _launch('ino'),
            ),
            Btn(
              label: 'Files',
              small: true,
              icon: LucideIcons.folder,
              onTap: () => _launch('files'),
            ),
          ],
        ),
      ],
    );
  }

  Widget _buildSignalApprovalCardContent(IcColors c) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Row(
          children: [
            Icon(LucideIcons.radio, size: 15, color: c.accent),
            const SizedBox(width: 7),
            Expanded(
              child: Text(
                'LIVE SMART APP SIGNAL',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: IcType.text(
                  11,
                  weight: FontWeight.w700,
                  color: c.textMuted,
                ),
              ),
            ),
            const SizedBox(width: 8),
            TagPill(
              label: _briefApproved ? 'Approved' : 'Needs you',
              tone: _briefApproved ? TagTone.done : TagTone.accent,
              icon: _briefApproved ? LucideIcons.check : LucideIcons.hand,
            ),
          ],
        ),
        const SizedBox(height: 10),
        Text(
          'Signal approval · Schedule update',
          style: IcType.text(14, weight: FontWeight.w600, color: c.textStrong),
        ),
        const SizedBox(height: 4),
        Text(
          'Pending confirmation detected from connected workspace channel.',
          style: IcType.text(12.5, color: c.textBody, height: 1.4),
        ),
        const SizedBox(height: 12),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            Btn(
              label: _briefApproved ? 'Updated in CRM' : 'Approve & Sync',
              small: true,
              primary: !_briefApproved,
              icon: LucideIcons.check,
              onTap: _briefApproved
                  ? null
                  : () {
                      setState(() => _briefApproved = true);
                      widget.onApproveSignal?.call();
                      _toast(
                        'Approved signal on neuron://local/os.confirmations/inbox',
                      );
                    },
            ),
            Btn(label: 'Open in Ino', small: true, onTap: () => _launch('ino')),
          ],
        ),
      ],
    );
  }

  Widget _buildIdentitySwitcherCardContent(IcColors c) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Row(
          children: [
            Icon(LucideIcons.palette, size: 15, color: c.accent),
            const SizedBox(width: 7),
            Expanded(
              child: Text(
                'UI IDENTITY & GLASS THEME',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: IcType.text(
                  11,
                  weight: FontWeight.w700,
                  color: c.textMuted,
                ),
              ),
            ),
            const SizedBox(width: 8),
            GestureDetector(
              onTap: widget.onToggleDark,
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: TagPill(
                  label: c.isDark ? 'Dark' : 'Light',
                  tone: TagTone.accent,
                  icon: c.isDark ? LucideIcons.moon : LucideIcons.sun,
                ),
              ),
            ),
          ],
        ),
        const SizedBox(height: 10),
        for (final preset in IcStylePreset.values)
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: _ThemePresetRow(
              preset: preset,
              selected: widget.stylePreset == preset,
              onTap: () => widget.onSelectPreset?.call(preset),
            ),
          ),
        const SizedBox(height: 8),
        Row(
          children: [
            Expanded(
              child: Text(
                'Bar layout',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: IcType.text(12, color: c.textMuted),
              ),
            ),
            const SizedBox(width: 8),
            GestureDetector(
              onTap: widget.onCycleBarLayout,
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: TagPill(
                  label: widget.barLayout.label,
                  tone: TagTone.neutral,
                  icon: LucideIcons.panelBottom,
                ),
              ),
            ),
          ],
        ),
      ],
    );
  }

  Widget _buildSmartAppGridCardContent(IcColors c) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Row(
          children: [
            Icon(LucideIcons.layoutGrid, size: 15, color: c.accent),
            const SizedBox(width: 7),
            Expanded(
              child: Text(
                'SMART APPS & WORKSPACE',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: IcType.text(
                  11,
                  weight: FontWeight.w700,
                  color: c.textMuted,
                ),
              ),
            ),
            const SizedBox(width: 8),
            Text(
              '${(widget.tokensToday / 1000).toStringAsFixed(1)}k tok',
              style: IcType.text(
                11.5,
                weight: FontWeight.w600,
                color: c.accent,
                tnum: true,
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),
        Wrap(
          spacing: 10,
          runSpacing: 10,
          children: [
            for (final spec in widget.apps)
              _SmartAppTile(
                spec: spec,
                onTap: () => _launch(spec.id),
                onCopyUri: () {
                  Clipboard.setData(ClipboardData(text: spec.neuronUri));
                  _toast('Copied ${spec.neuronUri}');
                },
              ),
          ],
        ),
        if (widget.onOpenAllApps != null) ...[
          const SizedBox(height: 12),
          Btn(
            key: const ValueKey('smart-apps-open-all-btn'),
            label: 'Browse All ${allSmartAppSpecs.length} Apps',
            small: true,
            primary: true,
            icon: LucideIcons.layoutGrid,
            onTap: widget.onOpenAllApps,
          ),
        ],
      ],
    );
  }
}

class _StickyWhiteboardCard extends StatelessWidget {
  const _StickyWhiteboardCard({
    required this.cardId,
    required this.pinned,
    required this.onTogglePin,
    required this.onClose,
    required this.onMove,
    required this.onResize,
    required this.child,
  });

  final String cardId;
  final bool pinned;
  final VoidCallback onTogglePin;
  final VoidCallback onClose;
  final ValueChanged<Offset> onMove;
  final ValueChanged<Offset> onResize;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return IcAcrylicBox(
      key: ValueKey('sticky-card-$cardId'),
      radius: IcRadius.card,
      padding: EdgeInsets.zero,
      borderColor: pinned ? c.accent : null,
      shadows: IcShadow.lg(c),
      child: Stack(
        fit: StackFit.expand,
        children: [
          Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // Sticky-note top drag bar + Pin button
              GestureDetector(
                key: ValueKey('sticky-card-drag-$cardId'),
                behavior: HitTestBehavior.opaque,
                onPanUpdate: (d) => onMove(d.delta),
                child: Container(
                  height: 28,
                  padding: const EdgeInsets.symmetric(horizontal: 12),
                  decoration: BoxDecoration(
                    color: pinned
                        ? c.accentTint.withValues(alpha: 0.45)
                        : c.surfaceSunk.withValues(alpha: 0.35),
                    border: Border(bottom: BorderSide(color: c.borderHairline)),
                  ),
                  child: Row(
                    children: [
                      Icon(
                        LucideIcons.gripHorizontal,
                        size: 13,
                        color: c.textMuted,
                      ),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(
                          pinned ? 'PINNED STICKY NOTE' : 'STICKY NOTE',
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.text(
                            10,
                            weight: FontWeight.w700,
                            color: pinned ? c.accent : c.textMuted,
                          ),
                        ),
                      ),
                      Tooltip(
                        message: pinned
                            ? 'Unpin sticky note'
                            : 'Pin sticky note (locks position)',
                        child: GestureDetector(
                          key: ValueKey('sticky-card-pin-$cardId'),
                          onTap: onTogglePin,
                          child: MouseRegion(
                            cursor: SystemMouseCursors.click,
                            child: Container(
                              width: 22,
                              height: 22,
                              decoration: BoxDecoration(
                                color: pinned
                                    ? c.accentTint
                                    : Colors.transparent,
                                borderRadius: BorderRadius.circular(
                                  IcRadius.xs,
                                ),
                              ),
                              child: Icon(
                                pinned ? LucideIcons.pin : LucideIcons.pinOff,
                                size: 12.5,
                                color: pinned ? c.accent : c.textMuted,
                              ),
                            ),
                          ),
                        ),
                      ),
                      const SizedBox(width: 4),
                      Tooltip(
                        message: 'Close sticky note',
                        child: GestureDetector(
                          key: ValueKey('sticky-card-close-$cardId'),
                          onTap: onClose,
                          child: MouseRegion(
                            cursor: SystemMouseCursors.click,
                            child: Container(
                              width: 22,
                              height: 22,
                              decoration: BoxDecoration(
                                borderRadius: BorderRadius.circular(
                                  IcRadius.xs,
                                ),
                              ),
                              child: Icon(
                                LucideIcons.x,
                                size: 12.5,
                                color: c.textMuted,
                              ),
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
              Expanded(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
                  child: child,
                ),
              ),
            ],
          ),
          // Bottom-right corner resize handle
          Positioned(
            right: 0,
            bottom: 0,
            width: 22,
            height: 22,
            child: MouseRegion(
              cursor: SystemMouseCursors.resizeUpLeftDownRight,
              child: GestureDetector(
                key: ValueKey('sticky-card-resize-$cardId'),
                behavior: HitTestBehavior.opaque,
                onPanUpdate: (d) => onResize(d.delta),
                child: Align(
                  alignment: Alignment.bottomRight,
                  child: Padding(
                    padding: const EdgeInsets.all(4),
                    child: Icon(
                      LucideIcons.moveDiagonal2,
                      size: 11,
                      color: c.textMuted,
                    ),
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _ThemePresetRow extends StatefulWidget {
  const _ThemePresetRow({
    required this.preset,
    required this.selected,
    required this.onTap,
  });

  final IcStylePreset preset;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_ThemePresetRow> createState() => _ThemePresetRowState();
}

class _ThemePresetRowState extends State<_ThemePresetRow> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: IcMotion.fast,
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
          decoration: BoxDecoration(
            color: widget.selected
                ? c.accentTint
                : (_hover ? c.surfaceSunk : const Color(0x00000000)),
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(
              color: widget.selected ? c.accent : const Color(0x00000000),
            ),
          ),
          child: Row(
            children: [
              Icon(
                widget.selected ? LucideIcons.circleCheck : LucideIcons.circle,
                size: 14,
                color: widget.selected ? c.accent : c.textMuted,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  widget.preset.label,
                  style: IcType.text(
                    13,
                    weight: widget.selected ? FontWeight.w600 : FontWeight.w500,
                    color: widget.selected ? c.textStrong : c.textBody,
                  ),
                ),
              ),
              Text(
                widget.preset.defaultBarLayout.label.split(' ').first,
                style: IcType.text(11, color: c.textMuted),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _SmartAppTile extends StatefulWidget {
  const _SmartAppTile({
    required this.spec,
    required this.onTap,
    required this.onCopyUri,
  });

  final SmartAppTileSpec spec;
  final VoidCallback onTap;
  final VoidCallback onCopyUri;

  @override
  State<_SmartAppTile> createState() => _SmartAppTileState();
}

class _SmartAppTileState extends State<_SmartAppTile> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: widget.onTap,
        onSecondaryTap: widget.onCopyUri,
        child: AnimatedContainer(
          duration: IcMotion.fast,
          width: 94,
          padding: const EdgeInsets.symmetric(vertical: 10, horizontal: 6),
          decoration: BoxDecoration(
            color: _hover ? c.accentTint : c.surfaceSunk,
            borderRadius: BorderRadius.circular(IcRadius.button),
            border: Border.all(color: _hover ? c.accent : c.borderHairline),
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              IcAppIcon(
                appId: widget.spec.id,
                icon: widget.spec.icon,
                accentHex: widget.spec.accentHex,
                size: 30,
              ),
              const SizedBox(height: 6),
              Text(
                widget.spec.name,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: IcType.text(
                  12,
                  weight: FontWeight.w600,
                  color: c.textStrong,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Static, architectural desktop wallpaper replacing the 3D neural graph on the
/// Home Screen (`SmartDesktop`). Supports subtle workspace panning and spatial
/// sector markers.
class IcDesktopWallpaper extends StatelessWidget {
  const IcDesktopWallpaper({
    super.key,
    this.stylePreset = IcStylePreset.fluentAcrylic,
    this.panOffset = Offset.zero,
    this.zoom = 1.0,
    this.dimmed = false,
  });

  final IcStylePreset stylePreset;
  final Offset panOffset;
  final double zoom;
  final bool dimmed;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return RepaintBoundary(
      child: AnimatedOpacity(
        duration: IcMotion.base,
        opacity: dimmed ? 0.84 : 1.0,
        child: CustomPaint(
          painter: _IcDesktopWallpaperPainter(
            colors: c,
            stylePreset: stylePreset,
            panOffset: panOffset,
            zoom: zoom,
          ),
          size: Size.infinite,
        ),
      ),
    );
  }
}

class _IcDesktopWallpaperPainter extends CustomPainter {
  _IcDesktopWallpaperPainter({
    required this.colors,
    required this.stylePreset,
    required this.panOffset,
    required this.zoom,
  });

  final IcColors colors;
  final IcStylePreset stylePreset;
  final Offset panOffset;
  final double zoom;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Offset.zero & size;

    // 1. Deep atmospheric base gradient
    final bgColors = colors.isDark
        ? <Color>[
            const Color(0xFF0A0E17),
            const Color(0xFF0F1626),
            const Color(0xFF0B101B),
          ]
        : <Color>[
            const Color(0xFFF4F6FA),
            const Color(0xFFEAF0F8),
            const Color(0xFFF7F8FC),
          ];
    canvas.drawRect(
      rect,
      Paint()
        ..shader = LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: bgColors,
        ).createShader(rect),
    );

    // 2. Soft aurora / radial atmospheric pools
    final glow1Center = Offset(
      size.width * 0.25 + (panOffset.dx * 0.15),
      size.height * 0.28 + (panOffset.dy * 0.15),
    );
    final glow2Center = Offset(
      size.width * 0.76 + (panOffset.dx * 0.15),
      size.height * 0.68 + (panOffset.dy * 0.15),
    );
    final glow3Center = Offset(
      size.width * 0.52 + (panOffset.dx * 0.1),
      size.height * 0.45 + (panOffset.dy * 0.1),
    );

    canvas.drawCircle(
      glow1Center,
      size.shortestSide * 0.58,
      Paint()
        ..shader = RadialGradient(
          colors: [
            colors.accent.withValues(alpha: colors.isDark ? 0.16 : 0.10),
            const Color(0x00000000),
          ],
        ).createShader(
          Rect.fromCircle(center: glow1Center, radius: size.shortestSide * 0.58),
        ),
    );

    canvas.drawCircle(
      glow2Center,
      size.shortestSide * 0.54,
      Paint()
        ..shader = RadialGradient(
          colors: [
            colors.secondaryGlow.withValues(alpha: colors.isDark ? 0.14 : 0.08),
            const Color(0x00000000),
          ],
        ).createShader(
          Rect.fromCircle(center: glow2Center, radius: size.shortestSide * 0.54),
        ),
    );

    canvas.drawCircle(
      glow3Center,
      size.shortestSide * 0.42,
      Paint()
        ..shader = RadialGradient(
          colors: [
            const Color(0xFF14B8A6).withValues(alpha: colors.isDark ? 0.08 : 0.05),
            const Color(0x00000000),
          ],
        ).createShader(
          Rect.fromCircle(center: glow3Center, radius: size.shortestSide * 0.42),
        ),
    );

    // 3. Spatial Workspace Blueprint Grid (shifts with panOffset)
    final step = (44.0 * zoom).clamp(24.0, 96.0);
    final minorPaint = Paint()
      ..color = (colors.isDark ? Colors.white : Colors.black)
          .withValues(alpha: colors.isDark ? 0.032 : 0.035)
      ..strokeWidth = 1.0;
    final majorPaint = Paint()
      ..color = colors.accent.withValues(alpha: colors.isDark ? 0.075 : 0.065)
      ..strokeWidth = 1.0;

    final startX = (panOffset.dx % step) - step;
    final startY = (panOffset.dy % step) - step;

    var colIdx = 0;
    for (double x = startX; x < size.width + step; x += step) {
      canvas.drawLine(
        Offset(x, 0),
        Offset(x, size.height),
        colIdx % 4 == 0 ? majorPaint : minorPaint,
      );
      colIdx++;
    }

    var rowIdx = 0;
    for (double y = startY; y < size.height + step; y += step) {
      canvas.drawLine(
        Offset(0, y),
        Offset(size.width, y),
        rowIdx % 4 == 0 ? majorPaint : minorPaint,
      );
      rowIdx++;
    }

    // 4. Subtle geometric horizon rings in center of workspace
    final center = Offset(
      size.width * 0.5 + panOffset.dx * 0.35,
      size.height * 0.5 + panOffset.dy * 0.35,
    );
    final ringPaint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.0
      ..color = colors.accent.withValues(alpha: colors.isDark ? 0.09 : 0.07);
    canvas.drawCircle(center, 150 * zoom, ringPaint);
    canvas.drawCircle(
      center,
      290 * zoom,
      ringPaint..color = colors.accent.withValues(alpha: colors.isDark ? 0.05 : 0.04),
    );
  }

  @override
  bool shouldRepaint(covariant _IcDesktopWallpaperPainter oldDelegate) =>
      oldDelegate.colors != colors ||
      oldDelegate.stylePreset != stylePreset ||
      oldDelegate.panOffset != panOffset ||
      oldDelegate.zoom != zoom;
}

