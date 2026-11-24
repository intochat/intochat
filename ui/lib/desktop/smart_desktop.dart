import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

export 'app_catalog_launcher.dart';
import 'app_catalog_launcher.dart';

/// Interactive Whiteboard Home Desk with 3D `BrainScene3D` orbital neural graph
/// and draggable, resizable sticky-note Smart Cards with Pinning and Auto-Arrange.
class SmartDesktop extends StatefulWidget {
  const SmartDesktop({
    super.key,
    this.hasOpenWindows = false,
    this.stylePreset = IcStylePreset.fluentAcrylic,
    this.barLayout = BarLayout.floatingGlass,
    this.tokensToday = 14800,
    this.apps = const [],
    this.totalAppsCount,
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
  });

  final bool hasOpenWindows;
  final IcStylePreset stylePreset;
  final BarLayout barLayout;
  final int tokensToday;
  final List<SmartAppTileSpec> apps;
  final int? totalAppsCount;
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

  @override
  State<SmartDesktop> createState() => SmartDesktopState();
}

class SmartDesktopState extends State<SmartDesktop> {
  static const List<String> cardOrder = [
    'launcher',
    'signal',
    'identity',
    'apps',
  ];

  static const Map<String, String> cardLabels = {
    'launcher': 'Ino Launcher',
    'signal': 'Live Signal',
    'identity': 'Theme & Identity',
    'apps': 'Smart Apps',
  };

  String _selectedNeuronId = 'ino';
  bool _briefApproved = false;
  bool _initializedBounds = false;
  bool _widgetsMenuOpen = false;

  final Map<String, Rect> _cardBounds = {};
  final Set<String> _pinnedCardIds = {};
  final Set<String> _localClosedCardIds = {};
  final Set<String> _localOpenedCardIds = {};

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
    if (_initializedBounds && _cardBounds.length == cardOrder.length) return;
    _initializedBounds = true;
    final rightX = math.max(404.0, canvas.width - 364.0);
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
            // 1. Live Interactive 3D DigitalBrain Layer on the Desktop
            Positioned.fill(
              child: BrainScene3D(
                selectedId: _selectedNeuronId,
                ambientOpacity: hasOpenWindows ? 0.72 : 0.96,
                showHud: !hasOpenWindows,
                onSelectNode: (node) {
                  setState(() => _selectedNeuronId = node.id);
                  if (node.appId != null) {
                    _launch(node.appId == 'chat' ? 'ino' : node.appId!);
                  } else {
                    _toast(
                      'Neuron ${node.neuronUri} (${node.signalsPerMin} sig/min)',
                    );
                  }
                },
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
                            label: 'All Apps (${widget.totalAppsCount ?? widget.apps.length})',
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
                          onTap: () => setState(
                            () => _widgetsMenuOpen = !_widgetsMenuOpen,
                          ),
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
                  if (_widgetsMenuOpen) ...[
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
                    child: switch (id) {
                      'launcher' => _buildLauncherCardContent(c),
                      'signal' => _buildSignalApprovalCardContent(c),
                      'identity' => _buildIdentitySwitcherCardContent(c),
                      'apps' => _buildSmartAppGridCardContent(c),
                      _ => const SizedBox.shrink(),
                    },
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
              label: '3D Creator',
              small: true,
              icon: LucideIcons.brainCircuit,
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
          'Nordlys Q4 Renewal · Close date shift',
          style: IcType.text(14, weight: FontWeight.w600, color: c.textStrong),
        ),
        const SizedBox(height: 4),
        Text(
          'Salesforce sync detected an email from Kari Holm confirming 15 Nov 2026 (480,000 NOK).',
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
            label: 'Browse All ${widget.totalAppsCount ?? widget.apps.length} Apps',
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
