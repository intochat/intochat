import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'window_frame.dart';

const double kUnifiedAppHeaderHeight = 42.0;

/// Unified 42px Window & App Header that combines OS window chrome and application header.
/// Eliminates redundant double headers.
///
/// - Left: App Icon + App Title + Chevron (taps open the App & Testing Menu)
/// - Center: Contextual tab strip or breadcrumb slot
/// - Right: Reload (with hard-restart option), Pin toggle, Zoom/Maximize, Close
/// - Header serves as the spatial drag handle. Double-tap toggles zoom.
class UnifiedAppHeader extends StatefulWidget {
  const UnifiedAppHeader({
    super.key,
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
    this.onFocus,
    this.onClose,
    this.onMinimize,
    this.menuOpen = false,
    this.onToggleMenu,
    this.tabs = const [],
    this.onMoveDelta,
  });

  final DesktopWindowState window;
  final bool focused;
  final bool zoomed;
  final bool canMove;
  final Size desk;
  final VoidCallback onToggleZoom;
  final VoidCallback onPin;
  final bool isPinned;
  final VoidCallback onReload;
  final VoidCallback onHardRestartSandbox;
  final VoidCallback? onFocus;
  final VoidCallback? onClose;
  final VoidCallback? onMinimize;
  final bool menuOpen;
  final VoidCallback? onToggleMenu;
  final List<DesktopTabSpec> tabs;
  final ValueChanged<Offset>? onMoveDelta;

  @override
  State<UnifiedAppHeader> createState() => _UnifiedAppHeaderState();
}

class _UnifiedAppHeaderState extends State<UnifiedAppHeader>
    with SingleTickerProviderStateMixin {
  late final AnimationController _spinController = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 600),
  );

  DateTime _lastTap = DateTime(0);
  Offset _lastTapPos = Offset.zero;

  @override
  void dispose() {
    _spinController.dispose();
    super.dispose();
  }

  void _onPointerDown(PointerDownEvent e) {
    final now = DateTime.now();
    final quick = now.difference(_lastTap) < const Duration(milliseconds: 350);
    final near = (e.position - _lastTapPos).distance < 6;
    _lastTap = now;
    _lastTapPos = e.position;
    if (quick && near && !widget.menuOpen) {
      _lastTap = DateTime(0);
      widget.onToggleZoom();
    } else {
      widget.onFocus?.call();
    }
  }

  void _triggerReload({bool hard = false}) {
    _spinController.forward(from: 0);
    if (hard) {
      widget.window.recordHardRestart();
      widget.onHardRestartSandbox();
    } else {
      widget.window.recordReload();
      widget.onReload();
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final w = widget.window;

    return Listener(
      onPointerDown: _onPointerDown,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onPanStart: widget.canMove ? (_) => widget.onFocus?.call() : null,
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
          height: kUnifiedAppHeaderHeight,
          decoration: BoxDecoration(
            color: widget.focused ? c.titleBarFocused : c.titleBarBlurred,
            border: Border(bottom: BorderSide(color: c.borderHairline)),
          ),
          padding: const EdgeInsets.symmetric(horizontal: 10),
          child: Row(
            children: [
              // 1. Left Cluster: App Identity + Options Menu Button
              _buildAppIdentityCluster(c, w),

              // 2. Middle Slot: Context Tabs (if multiple) or fluid space
              Expanded(
                child: widget.tabs.length > 1
                    ? _buildTabCluster(c)
                    : const SizedBox.shrink(),
              ),

              // 3. Right Cluster: Reload, Pin, Zoom, Close
              _buildActionCluster(c, w),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildAppIdentityCluster(IcColors c, DesktopWindowState w) {
    return GestureDetector(
      key: ValueKey('window-settings-btn-${w.id}'),
      onTap: widget.onToggleMenu,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 4),
          decoration: BoxDecoration(
            color: widget.menuOpen ? c.surfaceSunk : Colors.transparent,
            borderRadius: BorderRadius.circular(IcRadius.button),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              IcAppIcon(appId: w.appId, accentHex: w.accentHex, size: 19),
              const SizedBox(width: 8),
              Text(
                w.title,
                style: IcType.text(
                  12.5,
                  weight: widget.focused ? FontWeight.w700 : FontWeight.w600,
                  color: widget.focused ? c.textStrong : c.textBody,
                ),
              ),
              if (w.sandboxStatusBadge != null) ...[
                const SizedBox(width: 6),
                TagPill(
                  label: w.sandboxStatusBadge!,
                  tone: TagTone.accent,
                ),
              ],
              const SizedBox(width: 4),
              Icon(
                widget.menuOpen ? LucideIcons.chevronUp : LucideIcons.chevronDown,
                size: 13,
                color: widget.focused ? c.textMuted : c.textSubtle,
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildTabCluster(IcColors c) {
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          for (final t in widget.tabs)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 2),
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: GestureDetector(
                  onTap: t.onTap,
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                    decoration: BoxDecoration(
                      color: t.selected ? c.surfaceSunk : Colors.transparent,
                      borderRadius: BorderRadius.circular(IcRadius.pill),
                      border: t.selected
                          ? Border.all(color: c.borderInput)
                          : null,
                    ),
                    child: Text(
                      t.label,
                      style: IcType.text(
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
  }

  Widget _buildActionCluster(IcColors c, DesktopWindowState w) {
    final effectivePinned = widget.isPinned || w.isPinned;

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        // 1. Reload Button with spin animation (short click = reload, hold/long-press = hard restart)
        _HeaderActionButton(
          key: ValueKey('window-reload-btn-${w.id}'),
          tooltip: 'Reload viewport (long-press for hard restart)',
          icon: LucideIcons.rotateCw,
          spinAnimation: _spinController,
          color: c.textMuted,
          onTap: () => _triggerReload(hard: false),
          onLongPress: () => _triggerReload(hard: true),
        ),
        const SizedBox(width: 4),

        // 2. Pin Button
        _HeaderActionButton(
          key: ValueKey('window-pin-btn-${w.id}'),
          tooltip: effectivePinned ? 'Unpin from canvas' : 'Pin to canvas (locks location)',
          icon: effectivePinned ? LucideIcons.pinOff : LucideIcons.pin,
          active: effectivePinned,
          color: effectivePinned ? c.accent : c.textMuted,
          onTap: widget.onPin,
        ),
        const SizedBox(width: 4),

        // 3. Zoom / Maximize Toggle
        _HeaderActionButton(
          key: ValueKey('window-zoom-btn-${w.id}'),
          tooltip: widget.zoomed ? 'Restore size' : 'Maximize',
          icon: widget.zoomed ? LucideIcons.minimize2 : LucideIcons.maximize2,
          color: c.textMuted,
          onTap: widget.onToggleZoom,
        ),
        const SizedBox(width: 4),

        // 4. Close Button
        _HeaderActionButton(
          key: ValueKey('window-close-btn-${w.id}'),
          tooltip: 'Close window',
          icon: LucideIcons.x,
          color: c.error,
          hoverColor: c.error.withValues(alpha: 0.15),
          onTap: widget.onClose ?? () {},
        ),
      ],
    );
  }
}

class _HeaderActionButton extends StatefulWidget {
  const _HeaderActionButton({
    super.key,
    required this.tooltip,
    required this.icon,
    required this.color,
    required this.onTap,
    this.onLongPress,
    this.active = false,
    this.hoverColor,
    this.spinAnimation,
  });

  final String tooltip;
  final IconData icon;
  final Color color;
  final VoidCallback onTap;
  final VoidCallback? onLongPress;
  final bool active;
  final Color? hoverColor;
  final Animation<double>? spinAnimation;

  @override
  State<_HeaderActionButton> createState() => _HeaderActionButtonState();
}

class _HeaderActionButtonState extends State<_HeaderActionButton> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    Widget iconWidget = Icon(
      widget.icon,
      size: 14.5,
      color: widget.active ? widget.color : (_hover ? c.textStrong : widget.color),
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
            width: 28,
            height: 28,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: widget.active
                  ? widget.color.withValues(alpha: 0.14)
                  : (_hover ? (widget.hoverColor ?? c.surfaceSunk) : Colors.transparent),
              borderRadius: BorderRadius.circular(IcRadius.button),
            ),
            child: iconWidget,
          ),
        ),
      ),
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
    final c = IcTheme.of(context);

    return IcAcrylicBox(
      key: ValueKey('window-settings-popover-${window.id}'),
      padding: const EdgeInsets.all(12),
      borderRadius: BorderRadius.circular(IcRadius.card),
      shadows: IcShadow.xl(c),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // App Title + URI
          Row(
            children: [
              IcAppIcon(appId: window.appId, accentHex: window.accentHex, size: 24),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      window.title,
                      style: IcType.text(13, weight: FontWeight.w700, color: c.textStrong),
                    ),
                    Text(
                      window.neuronUri,
                      style: IcType.text(11, color: c.textMuted, tnum: true),
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
            style: IcType.text(10.5, weight: FontWeight.w700, color: c.textMuted),
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
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                    decoration: BoxDecoration(
                      color: executionMode == id ? c.accentTint : c.surfaceSunk.withValues(alpha: 0.55),
                      borderRadius: BorderRadius.circular(IcRadius.xs),
                      border: Border.all(
                        color: executionMode == id ? c.accent : c.borderHairline,
                      ),
                    ),
                    child: Row(
                      children: [
                        Icon(icon, size: 13, color: executionMode == id ? c.accent : c.textMuted),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            label,
                            style: IcType.text(
                              12,
                              weight: executionMode == id ? FontWeight.w600 : FontWeight.w500,
                              color: executionMode == id ? c.textStrong : c.textBody,
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
            style: IcType.text(10.5, weight: FontWeight.w700, color: c.textMuted),
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
                  label: '3D Creator',
                  small: true,
                  primary: true,
                  icon: LucideIcons.brainCircuit,
                  onTap: onInspectSandbox,
                ),
              ),
            ],
          ),
          if (onOpenAppSettings != null) ...[
            const SizedBox(height: 6),
            Btn(
              key: const ValueKey('window-open-app-settings-btn'),
              label: 'App Settings…',
              small: true,
              icon: LucideIcons.slidersHorizontal,
              onTap: onOpenAppSettings,
            ),
          ],
        ],
      ),
    );
  }
}
