import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'window_frame.dart';

const double kDesktopTitleBarHeight = 38.0;
const double kSpatialCardHeaderHeight = 42.0;

/// Visual chrome variant rendered by [WindowHeaderBar].
enum WindowChromeVariant {
  /// Free-floating desktop window title bar (`38px`) with macOS-style traffic
  /// lights on the left, centered app identity, and snap/reload/pin/options
  /// actions on the right.
  floating,

  /// Spatial canvas card header (`42px`) with clickable app identity + menu
  /// chevron on the left, inline tab strip in the middle, and
  /// reload/pin/zoom/close actions on the right.
  spatialCard,
}

/// Single cohesive action/command delegate bound once per window.
///
/// Owns all [DesktopWindowState] mutations (`togglePinned`, `recordReload`,
/// `recordHardRestart`, `setExecutionMode`) and host/shell dispatch so floating
/// windows and spatial canvas cards share one mutation path, consistent toast
/// feedback, and a single `reloadEpoch` increment.
class DesktopWindowActions {
  const DesktopWindowActions({
    required this.window,
    this.onFocus,
    this.onClose,
    this.onMinimize,
    this.onToggleZoom,
    this.onPin,
    this.onReload,
    this.onHardRestartSandbox,
    this.onSwitchExecutionMode,
    this.onInspectSandbox,
    this.onOpenAppSettings,
    this.onStateMutated,
    this.onToast,
  });

  final DesktopWindowState window;
  final VoidCallback? onFocus;
  final VoidCallback? onClose;
  final VoidCallback? onMinimize;
  final VoidCallback? onToggleZoom;
  final VoidCallback? onPin;
  final VoidCallback? onReload;
  final VoidCallback? onHardRestartSandbox;
  final ValueChanged<String>? onSwitchExecutionMode;
  final VoidCallback? onInspectSandbox;
  final VoidCallback? onOpenAppSettings;
  final VoidCallback? onStateMutated;
  final ValueChanged<String>? onToast;

  bool get canOpenAppSettings => onOpenAppSettings != null;

  void focus() => onFocus?.call();

  void close() => onClose?.call();

  void minimize() => onMinimize?.call();

  void toggleZoom() {
    if (onToggleZoom != null) {
      onToggleZoom!();
    } else {
      window.toggleZoom();
    }
  }

  void togglePin() {
    window.togglePinned();
    onPin?.call();
    onStateMutated?.call();
  }

  void reload() {
    window.recordReload();
    onReload?.call();
    onStateMutated?.call();
  }

  void hardRestartSandbox([BuildContext? context]) {
    window.recordHardRestart();
    onHardRestartSandbox?.call();
    onStateMutated?.call();
    _emitToast(context, 'Hard-restarted isolated sandbox for ${window.title}');
  }

  void switchExecutionMode(String mode, [BuildContext? context]) {
    window.setExecutionMode(mode);
    onSwitchExecutionMode?.call(mode);
    onStateMutated?.call();
    _emitToast(
      context,
      'Switched execution mode to ${DesktopWindowState.formatExecutionMode(mode)}',
    );
  }

  void inspectSandbox() => onInspectSandbox?.call();

  void openAppSettings() => onOpenAppSettings?.call();

  void _emitToast(BuildContext? context, String message) {
    if (onToast != null) {
      onToast!(message);
    } else if (context != null && context.mounted) {
      showKitToast(context, message);
    }
  }
}

/// Shared window header bar supporting both [WindowChromeVariant.floating] and
/// [WindowChromeVariant.spatialCard] chrome layouts.
///
/// Centralises double-tap-to-zoom pointer timing, drag-to-move gestures,
/// reload spin animation, sandbox status pill rendering, and action dispatch
/// through [DesktopWindowActions].
class WindowHeaderBar extends StatefulWidget {
  const WindowHeaderBar({
    super.key,
    required this.variant,
    required this.actions,
    required this.focused,
    required this.zoomed,
    required this.canMove,
    required this.desk,
    required this.menuOpen,
    required this.onToggleMenu,
    this.tabs = const [],
    this.onMoveDelta,
  });

  final WindowChromeVariant variant;
  final DesktopWindowActions actions;
  final bool focused;
  final bool zoomed;
  final bool canMove;
  final Size desk;
  final bool menuOpen;
  final VoidCallback onToggleMenu;
  final List<DesktopTabSpec> tabs;
  final ValueChanged<Offset>? onMoveDelta;

  @override
  State<WindowHeaderBar> createState() => _WindowHeaderBarState();
}

class _WindowHeaderBarState extends State<WindowHeaderBar>
    with SingleTickerProviderStateMixin {
  bool _hoverTrafficLights = false;
  late final AnimationController _spinController = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 600),
  );

  DateTime _lastPointerDown = DateTime(0);
  Offset _lastPointerPos = Offset.zero;

  DesktopWindowState get _window => widget.actions.window;

  @override
  void dispose() {
    _spinController.dispose();
    super.dispose();
  }

  void _onPointerDown(PointerDownEvent e) {
    final now = DateTime.now();
    final quick =
        now.difference(_lastPointerDown) < const Duration(milliseconds: 350);
    final near = (e.position - _lastPointerPos).distance < 6;
    _lastPointerDown = now;
    _lastPointerPos = e.position;
    if (quick && near && !_hoverTrafficLights && !widget.menuOpen) {
      _lastPointerDown = DateTime(0);
      widget.actions.toggleZoom();
    } else {
      widget.actions.focus();
    }
  }

  void _triggerReload({bool hard = false}) {
    _spinController.forward(from: 0);
    if (hard) {
      widget.actions.hardRestartSandbox(context);
    } else {
      widget.actions.reload();
    }
  }

  void _snapToHalf({required bool left}) {
    final w = _window;
    widget.actions.focus();
    if (w.zoomed) w.toggleZoom();
    final halfW = ((widget.desk.width - 36) / 2).clamp(
      320.0,
      widget.desk.width,
    );
    final fullH = (widget.desk.height - 24).clamp(260.0, widget.desk.height);
    final x = left
        ? 12.0
        : (widget.desk.width - halfW - 12).clamp(12.0, widget.desk.width);
    w.setBounds(Rect.fromLTWH(x, 12, halfW, fullH));
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final w = _window;
    final isFloating = widget.variant == WindowChromeVariant.floating;

    return Listener(
      onPointerDown: _onPointerDown,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onPanStart: widget.canMove ? (_) => widget.actions.focus() : null,
        onPanUpdate: widget.canMove
            ? (d) {
                if (widget.onMoveDelta != null) {
                  widget.onMoveDelta!(d.delta);
                } else {
                  w.moveBy(d.delta, widget.desk);
                }
              }
            : null,
        child: Container(
          height: isFloating
              ? kDesktopTitleBarHeight
              : kSpatialCardHeaderHeight,
          decoration: BoxDecoration(
            color: widget.focused ? c.titleBarFocused : c.titleBarBlurred,
            border: Border(bottom: BorderSide(color: c.borderHairline)),
          ),
          padding: EdgeInsets.symmetric(horizontal: isFloating ? 14 : 10),
          child: isFloating
              ? _buildFloatingChrome(c, w)
              : _buildSpatialCardChrome(c, w),
        ),
      ),
    );
  }

  Widget _buildFloatingChrome(UiColors c, DesktopWindowState w) {
    return Stack(
      alignment: Alignment.center,
      children: [
        // Left: three 12px traffic lights, 8px apart
        Align(
          alignment: Alignment.centerLeft,
          child: MouseRegion(
            onEnter: (_) => setState(() => _hoverTrafficLights = true),
            onExit: (_) => setState(() => _hoverTrafficLights = false),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                _TrafficLight(
                  key: ValueKey('window-close-btn-${w.id}'),
                  color: c.error,
                  active: widget.focused || _hoverTrafficLights,
                  glyph: LucideIcons.x,
                  showGlyph: _hoverTrafficLights,
                  label: 'Close',
                  onTap: widget.actions.close,
                ),
                const SizedBox(width: 8),
                _TrafficLight(
                  key: ValueKey('window-minimize-btn-${w.id}'),
                  color: c.working,
                  active: widget.focused || _hoverTrafficLights,
                  glyph: LucideIcons.minus,
                  showGlyph: _hoverTrafficLights,
                  label: 'Minimise',
                  onTap: widget.actions.minimize,
                ),
                const SizedBox(width: 8),
                _TrafficLight(
                  key: ValueKey('window-zoom-btn-${w.id}'),
                  color: c.done,
                  active: widget.focused || _hoverTrafficLights,
                  glyph: widget.zoomed
                      ? LucideIcons.minimize2
                      : LucideIcons.maximize2,
                  showGlyph: _hoverTrafficLights,
                  label: 'Zoom',
                  onTap: widget.actions.toggleZoom,
                ),
              ],
            ),
          ),
        ),
        // Centre: app icon + title 13/600 (+ optional sandbox status pill)
        Padding(
          padding: EdgeInsets.symmetric(
            horizontal: w.bounds.width >= 460 ? 148 : 96,
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              IcAppIcon(appId: w.appId, accentHex: w.accentHex, size: 16),
              const SizedBox(width: 7),
              Flexible(
                child: Text(
                  w.title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: UiType.textStyle(
                    13,
                    weight: FontWeight.w600,
                    color: widget.focused ? c.textStrong : c.textMuted,
                  ),
                ),
              ),
              if (w.sandboxStatusBadge != null) ...[
                const SizedBox(width: 6),
                TagPill(label: w.sandboxStatusBadge!, tone: TagTone.accent),
              ],
            ],
          ),
        ),
        // Right: Split workspace snap + OS tools overflow group
        Align(
          alignment: Alignment.centerRight,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (widget.canMove && w.bounds.width >= 460) ...[
                WindowChromeActionButton(
                  key: ValueKey('window-snap-left-btn-${w.id}'),
                  tooltip: 'Split workspace left half',
                  icon: LucideIcons.panelLeft,
                  color: c.textMuted,
                  onTap: () => _snapToHalf(left: true),
                ),
                const SizedBox(width: 2),
                WindowChromeActionButton(
                  key: ValueKey('window-snap-right-btn-${w.id}'),
                  tooltip: 'Split workspace right half',
                  icon: LucideIcons.panelRight,
                  color: c.textMuted,
                  onTap: () => _snapToHalf(left: false),
                ),
                const SizedBox(width: 2),
              ],
              WindowChromeActionButton(
                key: ValueKey('window-reload-btn-${w.id}'),
                tooltip: 'Reload viewport (long-press for hard restart)',
                icon: LucideIcons.rotateCw,
                spinAnimation: _spinController,
                color: c.textMuted,
                onTap: () => _triggerReload(hard: false),
                onLongPress: () => _triggerReload(hard: true),
              ),
              const SizedBox(width: 2),
              WindowChromeActionButton(
                key: ValueKey('window-pin-btn-${w.id}'),
                tooltip: w.isPinned
                    ? 'Unpin window'
                    : 'Pin window (always on top)',
                icon: w.isPinned ? LucideIcons.pinOff : LucideIcons.pin,
                active: w.isPinned,
                color: w.isPinned ? c.accent : c.textMuted,
                onTap: widget.actions.togglePin,
              ),
              const SizedBox(width: 2),
              WindowChromeActionButton(
                key: ValueKey('window-settings-btn-${w.id}'),
                tooltip: 'Window & Sandbox options',
                icon: LucideIcons.moreHorizontal,
                active: widget.menuOpen,
                color: widget.menuOpen ? c.accent : c.textMuted,
                onTap: widget.onToggleMenu,
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildSpatialCardChrome(UiColors c, DesktopWindowState w) {
    return Row(
      children: [
        // 1. Left Cluster: App Identity + Options Menu Button
        GestureDetector(
          key: ValueKey('window-settings-btn-${w.id}'),
          onTap: widget.onToggleMenu,
          child: MouseRegion(
            cursor: SystemMouseCursors.click,
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
              decoration: BoxDecoration(
                color: widget.menuOpen ? c.surfaceSunk : Colors.transparent,
                borderRadius: BorderRadius.circular(
                  UiTokens.of(context).button,
                ),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  IcAppIcon(appId: w.appId, accentHex: w.accentHex, size: 19),
                  const SizedBox(width: 8),
                  Text(
                    w.title,
                    style: UiType.textStyle(
                      12.5,
                      weight: widget.focused
                          ? FontWeight.w700
                          : FontWeight.w600,
                      color: widget.focused ? c.textStrong : c.textBody,
                    ),
                  ),
                  if (w.sandboxStatusBadge != null) ...[
                    const SizedBox(width: 6),
                    TagPill(label: w.sandboxStatusBadge!, tone: TagTone.accent),
                  ],
                  const SizedBox(width: 4),
                  Icon(
                    widget.menuOpen
                        ? LucideIcons.chevronUp
                        : LucideIcons.chevronDown,
                    size: 13,
                    color: widget.focused ? c.textMuted : c.textSubtle,
                  ),
                ],
              ),
            ),
          ),
        ),

        // 2. Middle Slot: Context Tabs (if multiple) or fluid space
        Expanded(
          child: widget.tabs.length > 1
              ? WindowTabStrip(tabs: widget.tabs)
              : const SizedBox.shrink(),
        ),

        // 3. Right Cluster: Reload, Pin, Zoom, Close
        Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            WindowChromeActionButton(
              key: ValueKey('window-reload-btn-${w.id}'),
              tooltip: 'Reload viewport (long-press for hard restart)',
              icon: LucideIcons.rotateCw,
              spinAnimation: _spinController,
              color: c.textMuted,
              size: 28,
              iconSize: 14.5,
              onTap: () => _triggerReload(hard: false),
              onLongPress: () => _triggerReload(hard: true),
            ),
            const SizedBox(width: 4),
            WindowChromeActionButton(
              key: ValueKey('window-pin-btn-${w.id}'),
              tooltip: w.isPinned
                  ? 'Unpin from canvas'
                  : 'Pin to canvas (locks location)',
              icon: w.isPinned ? LucideIcons.pinOff : LucideIcons.pin,
              active: w.isPinned,
              color: w.isPinned ? c.accent : c.textMuted,
              size: 28,
              iconSize: 14.5,
              onTap: widget.actions.togglePin,
            ),
            const SizedBox(width: 4),
            WindowChromeActionButton(
              key: ValueKey('window-zoom-btn-${w.id}'),
              tooltip: widget.zoomed ? 'Restore size' : 'Maximize',
              icon: widget.zoomed
                  ? LucideIcons.minimize2
                  : LucideIcons.maximize2,
              color: c.textMuted,
              size: 28,
              iconSize: 14.5,
              onTap: widget.actions.toggleZoom,
            ),
            const SizedBox(width: 4),
            WindowChromeActionButton(
              key: ValueKey('window-close-btn-${w.id}'),
              tooltip: 'Close window',
              icon: LucideIcons.x,
              color: c.error,
              hoverColor: c.error.withValues(alpha: 0.15),
              size: 28,
              iconSize: 14.5,
              onTap: widget.actions.close,
            ),
          ],
        ),
      ],
    );
  }
}

/// Standalone horizontal tab strip rendered inline inside spatial card headers
/// or as a dedicated `34px` bar below floating window title bars.
class WindowTabStrip extends StatelessWidget {
  const WindowTabStrip({
    super.key,
    required this.tabs,
    this.showBarContainer = false,
  });

  final List<DesktopTabSpec> tabs;
  final bool showBarContainer;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final strip = SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          for (final t in tabs)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 2),
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: GestureDetector(
                  onTap: t.onTap,
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 8,
                      vertical: 4,
                    ),
                    decoration: BoxDecoration(
                      color: t.selected ? c.surfaceSunk : Colors.transparent,
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).pill,
                      ),
                      border: t.selected
                          ? Border.all(color: c.borderInput)
                          : null,
                    ),
                    child: Text(
                      t.label,
                      style: UiType.textStyle(
                        11.5,
                        weight: t.selected ? FontWeight.w600 : FontWeight.w400,
                        color: t.selected ? c.textStrong : c.textMuted,
                      ),
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );

    if (!showBarContainer) return strip;

    return Container(
      height: 34,
      decoration: BoxDecoration(
        color: c.surfaceSunk.withValues(alpha: 0.45),
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      alignment: Alignment.centerLeft,
      child: strip,
    );
  }
}

/// Shared hoverable icon button used by both floating window title bars and
/// spatial card headers.
class WindowChromeActionButton extends StatefulWidget {
  const WindowChromeActionButton({
    super.key,
    required this.tooltip,
    required this.icon,
    required this.color,
    required this.onTap,
    this.onLongPress,
    this.active = false,
    this.hoverColor,
    this.spinAnimation,
    this.size = 24.0,
    this.iconSize = 13.5,
  });

  final String tooltip;
  final IconData icon;
  final Color color;
  final VoidCallback onTap;
  final VoidCallback? onLongPress;
  final bool active;
  final Color? hoverColor;
  final Animation<double>? spinAnimation;
  final double size;
  final double iconSize;

  @override
  State<WindowChromeActionButton> createState() =>
      _WindowChromeActionButtonState();
}

class _WindowChromeActionButtonState extends State<WindowChromeActionButton> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    Widget iconWidget = Icon(
      widget.icon,
      size: widget.iconSize,
      color: widget.active
          ? widget.color
          : (_hover ? c.textStrong : widget.color),
    );

    if (widget.spinAnimation != null) {
      iconWidget = RotationTransition(
        turns: widget.spinAnimation!,
        child: iconWidget,
      );
    }

    return Tooltip(
      message: widget.tooltip,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _hover = true),
        onExit: (_) => setState(() => _hover = false),
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTap: widget.onTap,
          onLongPress: widget.onLongPress,
          child: Container(
            width: widget.size,
            height: widget.size,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: widget.active
                  ? widget.color.withValues(alpha: 0.14)
                  : (_hover
                        ? (widget.hoverColor ?? c.surfaceSunk)
                        : Colors.transparent),
              borderRadius: BorderRadius.circular(UiTokens.of(context).button),
            ),
            child: iconWidget,
          ),
        ),
      ),
    );
  }
}

/// Shared host managing [AppTestingMenuPopover] positioning, action dispatch
/// through [DesktopWindowActions], and automatic dismissal.
class WindowTestingMenuHost extends StatelessWidget {
  const WindowTestingMenuHost({
    super.key,
    required this.actions,
    required this.open,
    required this.onDismiss,
    required this.topOffset,
    required this.child,
    this.alignment = Alignment.topRight,
    this.horizontalInset = 10.0,
    this.popoverWidth = 320.0,
    this.clipBehavior = Clip.none,
  });

  final DesktopWindowActions actions;
  final bool open;
  final VoidCallback onDismiss;
  final double topOffset;
  final Widget child;
  final Alignment alignment;
  final double horizontalInset;
  final double popoverWidth;
  final Clip clipBehavior;

  @override
  Widget build(BuildContext context) {
    final w = actions.window;
    final alignLeft = alignment.x < 0;

    return Stack(
      clipBehavior: clipBehavior,
      children: [
        child,
        if (open)
          Positioned(
            left: alignLeft ? horizontalInset : null,
            right: alignLeft ? null : horizontalInset,
            top: topOffset,
            width: popoverWidth,
            child: AppTestingMenuPopover(
              window: w,
              executionMode: w.executionMode,
              onSelectMode: (mode) {
                actions.switchExecutionMode(mode, context);
                onDismiss();
              },
              onReload: () {
                actions.reload();
                onDismiss();
              },
              onResetSandbox: () {
                actions.hardRestartSandbox(context);
                onDismiss();
              },
              onInspectSandbox: () {
                actions.inspectSandbox();
                onDismiss();
              },
              onOpenAppSettings: actions.canOpenAppSettings
                  ? () {
                      onDismiss();
                      actions.openAppSettings();
                    }
                  : null,
            ),
          ),
      ],
    );
  }
}

/// Floating options & testing menu anchored to the app window.
class AppTestingMenuPopover extends StatelessWidget {
  const AppTestingMenuPopover({
    super.key,
    required this.window,
    required this.executionMode,
    required this.onSelectMode,
    required this.onReload,
    required this.onResetSandbox,
    required this.onInspectSandbox,
    this.onOpenAppSettings,
    this.onRunTests,
  });

  final DesktopWindowState window;
  final String executionMode;
  final ValueChanged<String> onSelectMode;
  final VoidCallback onReload;
  final VoidCallback onResetSandbox;
  final VoidCallback onInspectSandbox;
  final VoidCallback? onOpenAppSettings;
  final VoidCallback? onRunTests;

  static const _modes = [
    ('wasm-ui', 'WASM Web UI', LucideIcons.globe),
    ('mcp', 'MCP Server', LucideIcons.server),
    ('native-linux', 'Native Linux CLI', LucideIcons.terminal),
  ];

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);

    return UiAcrylicSurface(
      key: ValueKey('window-settings-popover-${window.id}'),
      padding: const EdgeInsets.all(12),
      borderRadius: BorderRadius.circular(UiTokens.of(context).card),
      shadows: UiShadows.xl(c),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // App Title + URI
          Row(
            children: [
              IcAppIcon(
                appId: window.appId,
                accentHex: window.accentHex,
                size: 24,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      window.title,
                      style: UiType.textStyle(
                        13,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                    Text(
                      window.neuronUri,
                      style: UiType.textStyle(
                        11,
                        color: c.textMuted,
                        tnum: true,
                      ),
                    ),
                  ],
                ),
              ),
              TagPill(
                label: DesktopWindowState.formatExecutionMode(executionMode),
                tone: TagTone.accent,
              ),
            ],
          ),
          const SizedBox(height: 10),
          Divider(height: 1, color: c.borderHairline),
          const SizedBox(height: 8),

          // Execution Mode Switcher
          Text(
            'EXECUTION MODE',
            style: UiType.textStyle(
              10.5,
              weight: FontWeight.w700,
              color: c.textMuted,
            ),
          ),
          const SizedBox(height: 6),
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
                      vertical: 6,
                    ),
                    decoration: BoxDecoration(
                      color: executionMode == id
                          ? c.accentTint
                          : c.surfaceSunk.withValues(alpha: 0.55),
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).xs,
                      ),
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
                          size: 13,
                          color: executionMode == id ? c.accent : c.textMuted,
                        ),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            label,
                            style: UiType.textStyle(
                              12,
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

          // Testing & Sandbox Actions
          Text(
            'TESTING & SANDBOX CONTROLS',
            style: UiType.textStyle(
              10.5,
              weight: FontWeight.w700,
              color: c.textMuted,
            ),
          ),
          const SizedBox(height: 6),
          Row(
            children: [
              Expanded(
                child: UiMixButton.preset(
                  key: const ValueKey('window-sandbox-reset-btn'),
                  label: 'Reset Sandbox',
                  style: UiMixButtonStyle.secondary,
                  compact: true,
                  iconData: LucideIcons.rotateCcw,
                  onPressed: onResetSandbox,
                ),
              ),
              const SizedBox(width: 6),
              Expanded(
                child: UiMixButton.preset(
                  key: const ValueKey('window-sandbox-inspect-btn'),
                  label: '3D Creator',
                  style: UiMixButtonStyle.primary,
                  compact: true,
                  iconData: LucideIcons.brainCircuit,
                  onPressed: onInspectSandbox,
                ),
              ),
            ],
          ),
          if (onOpenAppSettings != null) ...[
            const SizedBox(height: 6),
            UiMixButton.preset(
              key: const ValueKey('window-open-app-settings-btn'),
              label: 'App Settings…',
              style: UiMixButtonStyle.secondary,
              compact: true,
              iconData: LucideIcons.slidersHorizontal,
              onPressed: onOpenAppSettings,
            ),
          ],
        ],
      ),
    );
  }
}

class _TrafficLight extends StatelessWidget {
  const _TrafficLight({
    super.key,
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
    final c = UiColors.of(context);
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
