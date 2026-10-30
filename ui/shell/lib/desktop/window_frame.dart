import 'dart:async';
import 'dart:ui';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

const double kDesktopTitleBarHeight = 38.0;

class DesktopWindowState extends ChangeNotifier {
  DesktopWindowState({
    required this.id,
    required this.appId,
    required this.title,
    required this.neuronUri,
    this.bounds = const Rect.fromLTWH(80, 48, 860, 560),
    this.zoomed = false,
    this.minimized = false,
    this.isPinned = false,
    this.executionMode = 'wasm-ui',
    this.accentHex,
  });

  final int id;
  final String appId;
  String title;
  final String neuronUri;
  Rect bounds;
  bool zoomed;
  bool minimized;
  bool isPinned;
  String executionMode;
  int reloadEpoch = 0;
  String? sandboxStatusBadge;
  final String? accentHex;

  static String formatExecutionMode(String mode) {
    return switch (mode.toLowerCase()) {
      'wasm-ui' => 'WASM Web UI',
      'mcp' => 'MCP Server',
      'native-linux' => 'Native Linux CLI',
      _ => mode,
    };
  }

  void moveBy(Offset delta, Size desk) {
    if (zoomed) return;
    final nextLeft = (bounds.left + delta.dx).clamp(
      0.0,
      (desk.width - 160).clamp(0.0, double.infinity),
    );
    final nextTop = (bounds.top + delta.dy).clamp(
      0.0,
      (desk.height - 80).clamp(0.0, double.infinity),
    );
    bounds = Rect.fromLTWH(nextLeft, nextTop, bounds.width, bounds.height);
    notifyListeners();
  }

  void resizeBy({
    required Offset delta,
    required bool left,
    required bool top,
    required bool right,
    required bool bottom,
    Size minSize = const Size(360, 260),
  }) {
    if (zoomed) return;
    var l = bounds.left;
    var t = bounds.top;
    var r = bounds.right;
    var b = bounds.bottom;
    if (left) {
      l = (l + delta.dx).clamp(0.0, r - minSize.width);
    }
    if (right) {
      r = (r + delta.dx).clamp(l + minSize.width, 4096.0);
    }
    if (top) {
      t = (t + delta.dy).clamp(0.0, b - minSize.height);
    }
    if (bottom) {
      b = (b + delta.dy).clamp(t + minSize.height, 4096.0);
    }
    bounds = Rect.fromLTRB(l, t, r, b);
    notifyListeners();
  }

  void setBounds(Rect nextBounds) {
    bounds = nextBounds;
    notifyListeners();
  }

  void toggleZoom() {
    zoomed = !zoomed;
    notifyListeners();
  }

  void setMinimized(bool value) {
    minimized = value;
    notifyListeners();
  }

  void setPinned(bool value) {
    isPinned = value;
    notifyListeners();
  }

  void togglePinned() {
    isPinned = !isPinned;
    notifyListeners();
  }

  void setExecutionMode(String mode) {
    executionMode = mode;
    sandboxStatusBadge = 'Mode: ${formatExecutionMode(mode)}';
    notifyListeners();
  }

  void recordReload() {
    reloadEpoch++;
    sandboxStatusBadge = 'Reloaded';
    notifyListeners();
  }

  void recordHardRestart() {
    reloadEpoch++;
    sandboxStatusBadge = 'Sandbox Restarted';
    notifyListeners();
  }
}

class DesktopTabSpec {
  const DesktopTabSpec({
    required this.key,
    required this.label,
    required this.selected,
    this.onTap,
  });

  final String key;
  final String label;
  final bool selected;
  final VoidCallback? onTap;
}

/// Free-floating, draggable, 8-way resizable Acrylic window frame (`38px` title
/// bar with Close/Minimise/Zoom traffic lights, Pin, Reload/Hard-Restart, and
/// Sandbox & Execution Mode Settings Popover).
class WindowFrame extends StatefulWidget {
  const WindowFrame({
    super.key,
    required this.window,
    required this.focused,
    required this.forceZoom,
    required this.desk,
    required this.child,
    this.tabs = const [],
    this.onFocus,
    this.onClose,
    this.onMinimize,
    this.onToggleZoom,
    this.onNewTab,
    this.onFavourite,
    this.isFavourite = false,
    this.onPin,
    this.isPinned = false,
    this.onReload,
    this.onHardRestartSandbox,
    this.onSwitchExecutionMode,
    this.onInspectSandbox,
    this.executionMode,
  });

  final DesktopWindowState window;
  final bool focused;
  final bool forceZoom;
  final Size desk;
  final Widget child;
  final List<DesktopTabSpec> tabs;
  final VoidCallback? onFocus;
  final VoidCallback? onClose;
  final VoidCallback? onMinimize;
  final VoidCallback? onToggleZoom;
  final VoidCallback? onNewTab;
  final VoidCallback? onFavourite;
  final bool isFavourite;
  final VoidCallback? onPin;
  final bool isPinned;
  final VoidCallback? onReload;
  final VoidCallback? onHardRestartSandbox;
  final ValueChanged<String>? onSwitchExecutionMode;
  final VoidCallback? onInspectSandbox;
  final String? executionMode;

  static final Map<int, bool> _lastZoom = {};

  @override
  State<WindowFrame> createState() => _WindowFrameState();
}

class _WindowFrameState extends State<WindowFrame> {
  bool _settingsOpen = false;

  void _handlePin() {
    widget.window.togglePinned();
    widget.onPin?.call();
  }

  void _handleReload() {
    widget.window.recordReload();
    widget.onReload?.call();
  }

  void _handleHardRestartSandbox() {
    widget.window.recordHardRestart();
    widget.onHardRestartSandbox?.call();
    if (mounted) {
      showKitToast(
        context,
        'Hard-restarted isolated sandbox for ${widget.window.title}',
      );
    }
  }

  void _handleSelectExecutionMode(String mode) {
    widget.window.setExecutionMode(mode);
    widget.onSwitchExecutionMode?.call(mode);
    setState(() => _settingsOpen = false);
  }

  void _handleInspectSandbox() {
    setState(() => _settingsOpen = false);
    widget.onInspectSandbox?.call();
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: widget.window,
      builder: (context, _) {
        final c = IcTheme.of(context);
        final w = widget.window;
        final zoomed = w.zoomed || widget.forceZoom;
        final radius = zoomed ? 0.0 : IcRadius.card;
        final effectivePinned = widget.isPinned || w.isPinned;
        final effectiveMode = widget.executionMode ?? w.executionMode;

        final innerContent = Stack(
          clipBehavior: Clip.none,
          children: [
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                _TitleBar(
                  window: w,
                  focused: widget.focused,
                  zoomed: zoomed,
                  canMove: !widget.forceZoom,
                  desk: widget.desk,
                  onFocus: widget.onFocus,
                  onClose: widget.onClose,
                  onMinimize: widget.onMinimize,
                  onToggleZoom: widget.onToggleZoom ?? w.toggleZoom,
                  onNewTab: widget.onNewTab,
                  onFavourite: widget.onFavourite,
                  isFavourite: widget.isFavourite,
                  onPin: _handlePin,
                  isPinned: effectivePinned,
                  onReload: _handleReload,
                  onHardRestartSandbox: _handleHardRestartSandbox,
                  settingsOpen: _settingsOpen,
                  onToggleSettings: () =>
                      setState(() => _settingsOpen = !_settingsOpen),
                ),
                if (widget.tabs.length > 1) _WindowTabStrip(tabs: widget.tabs),
                Expanded(
                  child: Listener(
                    behavior: HitTestBehavior.translucent,
                    onPointerDown: (_) {
                      if (!widget.focused) widget.onFocus?.call();
                    },
                    child: RepaintBoundary(child: widget.child),
                  ),
                ),
              ],
            ),
            if (_settingsOpen)
              Positioned(
                top: kDesktopTitleBarHeight + 6,
                right: 12,
                width: 268,
                child: _WindowSettingsPopover(
                  window: w,
                  executionMode: effectiveMode,
                  onSelectMode: _handleSelectExecutionMode,
                  onResetSandbox: () {
                    setState(() => _settingsOpen = false);
                    _handleHardRestartSandbox();
                  },
                  onInspectSandbox: _handleInspectSandbox,
                ),
              ),
          ],
        );

        final borderColor = effectivePinned
            ? c.accent
            : (widget.focused
                  ? (c.isGlass ? c.specularBorder : c.borderInput)
                  : c.borderHairline);

        final frame = Container(
          decoration: BoxDecoration(
            color: c.isGlass
                ? c.surfaceCard.withValues(alpha: c.windowOpacity)
                : c.surfaceCard,
            borderRadius: BorderRadius.circular(radius),
            border: Border.all(color: borderColor, width: zoomed ? 0 : 1),
            boxShadow: zoomed
                ? null
                : (widget.focused ? IcShadow.xl(c) : IcShadow.lg(c)),
          ),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(radius == 0 ? 0 : radius - 1),
            child: c.isGlass && c.blurSigma > 0
                ? BackdropFilter(
                    filter: ImageFilter.blur(
                      sigmaX: c.blurSigma,
                      sigmaY: c.blurSigma,
                    ),
                    child: innerContent,
                  )
                : innerContent,
          ),
        );

        final body = RepaintBoundary(
          child: zoomed
              ? frame
              : _ResizeHandles(
                  window: w,
                  onFocus: widget.onFocus,
                  child: frame,
                ),
        );

        final rect = zoomed ? Offset.zero & widget.desk : w.bounds;
        return AnimatedPositioned(
          duration:
              zoomed != WindowFrame._lastZoom.putIfAbsent(w.id, () => zoomed)
              ? IcMotion.base
              : Duration.zero,
          curve: IcMotion.ease,
          left: rect.left,
          top: rect.top,
          width: rect.width,
          height: rect.height,
          onEnd: () => WindowFrame._lastZoom[w.id] = zoomed,
          child: body,
        );
      },
    );
  }
}

class _TitleBar extends StatefulWidget {
  const _TitleBar({
    required this.window,
    required this.focused,
    required this.zoomed,
    required this.canMove,
    required this.desk,
    required this.onToggleZoom,
    required this.onPin,
    required this.isPinned,
    required this.onReload,
    required this.onHardRestartSandbox,
    required this.settingsOpen,
    required this.onToggleSettings,
    this.onFocus,
    this.onClose,
    this.onMinimize,
    this.onNewTab,
    this.onFavourite,
    this.isFavourite = false,
  });

  final DesktopWindowState window;
  final bool focused;
  final bool zoomed;
  final bool canMove;
  final Size desk;
  final VoidCallback? onFocus;
  final VoidCallback? onClose;
  final VoidCallback? onMinimize;
  final VoidCallback onToggleZoom;
  final VoidCallback? onNewTab;
  final VoidCallback? onFavourite;
  final bool isFavourite;
  final VoidCallback onPin;
  final bool isPinned;
  final VoidCallback onReload;
  final VoidCallback onHardRestartSandbox;
  final bool settingsOpen;
  final VoidCallback onToggleSettings;

  @override
  State<_TitleBar> createState() => _TitleBarState();
}

class _TitleBarState extends State<_TitleBar> {
  bool _hoverControls = false;
  DateTime _lastDown = DateTime(0);
  Offset _lastPos = Offset.zero;

  void _onDown(PointerDownEvent e) {
    final now = DateTime.now();
    final quick = now.difference(_lastDown) < const Duration(milliseconds: 350);
    final near = (e.position - _lastPos).distance < 6;
    _lastDown = now;
    _lastPos = e.position;
    if (quick && near && !_hoverControls) {
      _lastDown = DateTime(0);
      widget.onToggleZoom();
    } else {
      widget.onFocus?.call();
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final w = widget.window;
    return Listener(
      onPointerDown: _onDown,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onPanStart: widget.canMove ? (_) => widget.onFocus?.call() : null,
        onPanUpdate: widget.canMove
            ? (d) => w.moveBy(d.delta, widget.desk)
            : null,
        child: Container(
          height: kDesktopTitleBarHeight,
          decoration: BoxDecoration(
            color: widget.focused ? c.titleBarFocused : c.titleBarBlurred,
            border: Border(bottom: BorderSide(color: c.borderHairline)),
          ),
          padding: const EdgeInsets.symmetric(horizontal: 14),
          child: Stack(
            alignment: Alignment.center,
            children: [
              Align(
                alignment: Alignment.centerLeft,
                child: MouseRegion(
                  onEnter: (_) => setState(() => _hoverControls = true),
                  onExit: (_) => setState(() => _hoverControls = false),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      _Light(
                        color: c.error,
                        active: widget.focused || _hoverControls,
                        glyph: LucideIcons.x,
                        showGlyph: _hoverControls,
                        label: 'Close',
                        onTap: widget.onClose ?? () {},
                      ),
                      const SizedBox(width: 8),
                      _Light(
                        color: c.working,
                        active: widget.focused || _hoverControls,
                        glyph: LucideIcons.minus,
                        showGlyph: _hoverControls,
                        label: 'Minimise',
                        onTap:
                            widget.onMinimize ??
                            () => w.setMinimized(!w.minimized),
                      ),
                      const SizedBox(width: 8),
                      _Light(
                        color: c.done,
                        active: widget.focused || _hoverControls,
                        glyph: widget.zoomed
                            ? LucideIcons.minimize2
                            : LucideIcons.maximize2,
                        showGlyph: _hoverControls,
                        label: 'Zoom',
                        onTap: widget.onToggleZoom,
                      ),
                    ],
                  ),
                ),
              ),
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 120),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    IcAppIcon(appId: w.appId, accentHex: w.accentHex, size: 18),
                    const SizedBox(width: 7),
                    Flexible(
                      child: Text(
                        w.title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: IcType.text(
                          13,
                          weight: FontWeight.w600,
                          color: widget.focused ? c.textStrong : c.textMuted,
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    TagPill(label: w.neuronUri, tone: TagTone.neutral),
                    if (w.sandboxStatusBadge != null) ...[
                      const SizedBox(width: 6),
                      TagPill(
                        key: ValueKey('window-sandbox-badge-${w.id}'),
                        label: w.sandboxStatusBadge!,
                        tone: TagTone.accent,
                      ),
                    ],
                  ],
                ),
              ),
              Align(
                alignment: Alignment.centerRight,
                child: MouseRegion(
                  onEnter: (_) => setState(() => _hoverControls = true),
                  onExit: (_) => setState(() => _hoverControls = false),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      if (widget.onFavourite != null)
                        _TitleActionButton(
                          icon: LucideIcons.star,
                          tooltip: widget.isFavourite
                              ? 'Remove from favourites'
                              : 'Add to favourites',
                          active: widget.isFavourite,
                          onTap: widget.onFavourite!,
                        ),
                      _TitleActionButton(
                        key: ValueKey('window-pin-btn-${w.id}'),
                        icon: widget.isPinned
                            ? LucideIcons.pin
                            : LucideIcons.pinOff,
                        tooltip: widget.isPinned
                            ? 'Unpin window (always on top)'
                            : 'Pin window (always on top & lock in Auto-Arrange)',
                        active: widget.isPinned,
                        onTap: widget.onPin,
                      ),
                      _ReloadHoldActionButton(
                        key: ValueKey('window-reload-btn-${w.id}'),
                        onShortTap: widget.onReload,
                        onLongHoldOrSecondary: widget.onHardRestartSandbox,
                      ),
                      _TitleActionButton(
                        key: ValueKey('window-settings-btn-${w.id}'),
                        icon: LucideIcons.slidersHorizontal,
                        tooltip: 'Execution mode & sandbox settings',
                        active: widget.settingsOpen,
                        onTap: widget.onToggleSettings,
                      ),
                      if (widget.onNewTab != null)
                        _TitleActionButton(
                          icon: LucideIcons.plus,
                          tooltip: 'New tab',
                          onTap: widget.onNewTab!,
                        ),
                    ],
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

class _ReloadHoldActionButton extends StatefulWidget {
  const _ReloadHoldActionButton({
    super.key,
    required this.onShortTap,
    required this.onLongHoldOrSecondary,
  });

  final VoidCallback onShortTap;
  final VoidCallback onLongHoldOrSecondary;

  @override
  State<_ReloadHoldActionButton> createState() =>
      _ReloadHoldActionButtonState();
}

class _ReloadHoldActionButtonState extends State<_ReloadHoldActionButton> {
  static const Duration _holdThreshold = Duration(milliseconds: 500);

  bool _hover = false;
  bool _holding = false;
  bool _longPressFired = false;
  Timer? _holdTimer;

  @override
  void dispose() {
    _holdTimer?.cancel();
    super.dispose();
  }

  void _handlePointerDown(PointerDownEvent event) {
    if (event.buttons == kSecondaryMouseButton) {
      _holdTimer?.cancel();
      _longPressFired = true;
      widget.onLongHoldOrSecondary();
      return;
    }
    _longPressFired = false;
    setState(() => _holding = true);
    _holdTimer?.cancel();
    _holdTimer = Timer(_holdThreshold, () {
      _longPressFired = true;
      if (mounted) {
        setState(() => _holding = false);
      }
      widget.onLongHoldOrSecondary();
    });
  }

  void _handlePointerUp(PointerUpEvent event) {
    final wasLong = _longPressFired;
    _holdTimer?.cancel();
    _holdTimer = null;
    if (mounted && _holding) {
      setState(() => _holding = false);
    }
    if (!wasLong && event.buttons != kSecondaryMouseButton) {
      widget.onShortTap();
    }
  }

  void _handlePointerCancel(PointerCancelEvent event) {
    _holdTimer?.cancel();
    _holdTimer = null;
    if (mounted && _holding) {
      setState(() => _holding = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Tooltip(
      message: 'Reload view (Hold >500ms to hard-restart sandbox)',
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _hover = true),
        onExit: (_) => setState(() => _hover = false),
        child: Listener(
          behavior: HitTestBehavior.opaque,
          onPointerDown: _handlePointerDown,
          onPointerUp: _handlePointerUp,
          onPointerCancel: _handlePointerCancel,
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTap: () {},
            child: Container(
              width: 26,
              height: 26,
              margin: const EdgeInsets.only(left: 4),
              decoration: BoxDecoration(
                color: _holding
                    ? c.accentTint
                    : (_hover ? c.surfaceSunk : Colors.transparent),
                borderRadius: BorderRadius.circular(IcRadius.xs),
              ),
              child: Icon(
                LucideIcons.rotateCw,
                size: 13.5,
                color: _holding
                    ? c.accent
                    : (_hover ? c.textStrong : c.textMuted),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _WindowSettingsPopover extends StatelessWidget {
  const _WindowSettingsPopover({
    required this.window,
    required this.executionMode,
    required this.onSelectMode,
    required this.onResetSandbox,
    required this.onInspectSandbox,
  });

  final DesktopWindowState window;
  final String executionMode;
  final ValueChanged<String> onSelectMode;
  final VoidCallback onResetSandbox;
  final VoidCallback onInspectSandbox;

  static const _modes = <(String id, String label, IconData icon)>[
    ('wasm-ui', 'WASM Web UI', LucideIcons.globe),
    ('mcp', 'MCP Server', LucideIcons.plug),
    ('native-linux', 'Native Linux CLI', LucideIcons.terminal),
  ];

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return IcAcrylicBox(
      key: ValueKey('window-settings-popover-${window.id}'),
      padding: const EdgeInsets.all(14),
      borderRadius: BorderRadius.circular(IcRadius.card),
      shadows: IcShadow.xl(c),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Icon(LucideIcons.slidersHorizontal, size: 14, color: c.accent),
              const SizedBox(width: 7),
              Expanded(
                child: Text(
                  'EXECUTION MODE',
                  style: IcType.text(
                    11,
                    weight: FontWeight.w700,
                    color: c.textMuted,
                  ),
                ),
              ),
              TagPill(
                label: DesktopWindowState.formatExecutionMode(executionMode),
                tone: TagTone.accent,
              ),
            ],
          ),
          const SizedBox(height: 8),
          for (final (id, label, icon) in _modes)
            Padding(
              padding: const EdgeInsets.only(bottom: 4),
              child: GestureDetector(
                key: ValueKey('exec-mode-$id'),
                onTap: () => onSelectMode(id),
                child: MouseRegion(
                  cursor: SystemMouseCursors.click,
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 10,
                      vertical: 7,
                    ),
                    decoration: BoxDecoration(
                      color: executionMode == id
                          ? c.accentTint
                          : c.surfaceSunk.withValues(alpha: 0.55),
                      borderRadius: BorderRadius.circular(IcRadius.xs),
                      border: Border.all(
                        color: executionMode == id
                            ? c.accent
                            : c.borderHairline,
                      ),
                    ),
                    child: Row(
                      children: [
                        Icon(
                          icon,
                          size: 13.5,
                          color: executionMode == id ? c.accent : c.textMuted,
                        ),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            label,
                            style: IcType.text(
                              12.5,
                              weight: executionMode == id
                                  ? FontWeight.w600
                                  : FontWeight.w500,
                              color: executionMode == id
                                  ? c.textStrong
                                  : c.textBody,
                            ),
                          ),
                        ),
                        if (executionMode == id)
                          Icon(LucideIcons.check, size: 13, color: c.accent),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          const SizedBox(height: 8),
          Divider(height: 1, color: c.borderHairline),
          const SizedBox(height: 8),
          Text(
            'ISOLATED SANDBOX',
            style: IcType.text(
              10.5,
              weight: FontWeight.w700,
              color: c.textMuted,
            ),
          ),
          const SizedBox(height: 6),
          Row(
            children: [
              Expanded(
                child: Btn(
                  key: const ValueKey('window-sandbox-reset-btn'),
                  label: 'Reset Sandbox',
                  small: true,
                  icon: LucideIcons.rotateCcw,
                  onTap: onResetSandbox,
                ),
              ),
              const SizedBox(width: 6),
              Expanded(
                child: Btn(
                  key: const ValueKey('window-sandbox-inspect-btn'),
                  label: 'Inspect in 3D Creator',
                  small: true,
                  primary: true,
                  icon: LucideIcons.brainCircuit,
                  onTap: onInspectSandbox,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _TitleActionButton extends StatefulWidget {
  const _TitleActionButton({
    super.key,
    required this.icon,
    required this.tooltip,
    required this.onTap,
    this.active = false,
  });

  final IconData icon;
  final String tooltip;
  final VoidCallback onTap;
  final bool active;

  @override
  State<_TitleActionButton> createState() => _TitleActionButtonState();
}

class _TitleActionButtonState extends State<_TitleActionButton> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Tooltip(
      message: widget.tooltip,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _hover = true),
        onExit: (_) => setState(() => _hover = false),
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: widget.onTap,
          child: Container(
            width: 26,
            height: 26,
            margin: const EdgeInsets.only(left: 4),
            decoration: BoxDecoration(
              color: widget.active
                  ? c.accentTint
                  : (_hover ? c.surfaceSunk : Colors.transparent),
              borderRadius: BorderRadius.circular(IcRadius.xs),
            ),
            child: Icon(
              widget.icon,
              size: 13.5,
              color: widget.active
                  ? c.accent
                  : (_hover ? c.textStrong : c.textMuted),
            ),
          ),
        ),
      ),
    );
  }
}

class _WindowTabStrip extends StatelessWidget {
  const _WindowTabStrip({required this.tabs});

  final List<DesktopTabSpec> tabs;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Container(
      height: 32,
      decoration: BoxDecoration(
        color: c.surfaceSunk.withValues(alpha: 0.72),
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      child: ListView.separated(
        scrollDirection: Axis.horizontal,
        itemCount: tabs.length,
        separatorBuilder: (_, _) => const SizedBox(width: 4),
        itemBuilder: (context, index) {
          final tab = tabs[index];
          return GestureDetector(
            onTap: tab.onTap,
            child: MouseRegion(
              cursor: SystemMouseCursors.click,
              child: Container(
                padding: const EdgeInsets.symmetric(horizontal: 10),
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: tab.selected ? c.surfaceCard : Colors.transparent,
                  borderRadius: BorderRadius.circular(IcRadius.xs),
                  border: tab.selected
                      ? Border.all(color: c.borderHairline)
                      : null,
                ),
                child: Text(
                  tab.label,
                  style: IcType.text(
                    12,
                    weight: tab.selected ? FontWeight.w600 : FontWeight.w500,
                    color: tab.selected ? c.textStrong : c.textMuted,
                  ),
                ),
              ),
            ),
          );
        },
      ),
    );
  }
}

class _Light extends StatelessWidget {
  const _Light({
    required this.color,
    required this.active,
    required this.glyph,
    required this.showGlyph,
    required this.label,
    required this.onTap,
  });

  final Color color;
  final bool active;
  final IconData glyph;
  final bool showGlyph;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Semantics(
      button: true,
      label: label,
      child: GestureDetector(
        onTap: onTap,
        onDoubleTap: () {},
        child: MouseRegion(
          cursor: SystemMouseCursors.click,
          child: Container(
            width: 12,
            height: 12,
            decoration: BoxDecoration(
              color: active ? color : c.borderInput,
              shape: BoxShape.circle,
            ),
            child: showGlyph
                ? Icon(glyph, size: 8, color: const Color(0xB317140F))
                : null,
          ),
        ),
      ),
    );
  }
}

class _ResizeHandles extends StatelessWidget {
  const _ResizeHandles({
    required this.window,
    required this.child,
    this.onFocus,
  });

  final DesktopWindowState window;
  final Widget child;
  final VoidCallback? onFocus;

  static const _edge = 6.0;
  static const _corner = 14.0;

  @override
  Widget build(BuildContext context) {
    Widget handle({
      required MouseCursor cursor,
      double? left,
      double? top,
      double? right,
      double? bottom,
      double? width,
      double? height,
      required bool l,
      required bool t,
      required bool r,
      required bool b,
    }) {
      return Positioned(
        left: left,
        top: top,
        right: right,
        bottom: bottom,
        width: width,
        height: height,
        child: MouseRegion(
          cursor: cursor,
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onPanStart: (_) => onFocus?.call(),
            onPanUpdate: (d) => window.resizeBy(
              delta: d.delta,
              left: l,
              top: t,
              right: r,
              bottom: b,
            ),
          ),
        ),
      );
    }

    return Stack(
      children: [
        Positioned.fill(child: child),
        handle(
          cursor: SystemMouseCursors.resizeLeftRight,
          left: 0,
          top: _corner,
          bottom: _corner,
          width: _edge,
          l: true,
          t: false,
          r: false,
          b: false,
        ),
        handle(
          cursor: SystemMouseCursors.resizeLeftRight,
          right: 0,
          top: _corner,
          bottom: _corner,
          width: _edge,
          l: false,
          t: false,
          r: true,
          b: false,
        ),
        handle(
          cursor: SystemMouseCursors.resizeUpDown,
          top: 0,
          left: _corner,
          right: _corner,
          height: _edge,
          l: false,
          t: true,
          r: false,
          b: false,
        ),
        handle(
          cursor: SystemMouseCursors.resizeUpDown,
          bottom: 0,
          left: _corner,
          right: _corner,
          height: _edge,
          l: false,
          t: false,
          r: false,
          b: true,
        ),
        handle(
          cursor: SystemMouseCursors.resizeUpLeftDownRight,
          left: 0,
          top: 0,
          width: _corner,
          height: _corner,
          l: true,
          t: true,
          r: false,
          b: false,
        ),
        handle(
          cursor: SystemMouseCursors.resizeUpRightDownLeft,
          right: 0,
          top: 0,
          width: _corner,
          height: _corner,
          l: false,
          t: true,
          r: true,
          b: false,
        ),
        handle(
          cursor: SystemMouseCursors.resizeUpRightDownLeft,
          left: 0,
          bottom: 0,
          width: _corner,
          height: _corner,
          l: true,
          t: false,
          r: false,
          b: true,
        ),
        handle(
          cursor: SystemMouseCursors.resizeUpLeftDownRight,
          right: 0,
          bottom: 0,
          width: _corner,
          height: _corner,
          l: false,
          t: false,
          r: true,
          b: true,
        ),
      ],
    );
  }
}
