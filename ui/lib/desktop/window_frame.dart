import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'window_chrome.dart';

export 'window_chrome.dart';

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
    this.actions,
    this.tabs = const [],
  });

  final DesktopWindowState window;
  final bool focused;
  final bool forceZoom;
  final Size desk;
  final Widget child;
  final DesktopWindowActions? actions;
  final List<DesktopTabSpec> tabs;

  static final Map<int, bool> _lastZoom = {};

  @override
  State<WindowFrame> createState() => _WindowFrameState();
}

class _WindowFrameState extends State<WindowFrame> {
  bool _menuOpen = false;
  final GlobalKey _focusKey = GlobalKey();
  late final String _focusTarget = 'window:${widget.window.appId}';

  DesktopWindowActions get _actions =>
      widget.actions ?? DesktopWindowActions(window: widget.window);

  @override
  void initState() {
    super.initState();
    FocusRegistry.instance.register(_focusTarget, _focusKey);
  }

  @override
  void dispose() {
    FocusRegistry.instance.unregister(_focusTarget, _focusKey);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: widget.window,
      builder: (context, _) {
        final c = UiColors.of(context);
        final w = widget.window;
        final actions = _actions;
        final zoomed = w.zoomed || widget.forceZoom;
        final radius = zoomed ? 0.0 : UiTokens.of(context).card;

        final innerContent = Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            WindowHeaderBar(
              variant: WindowChromeVariant.floating,
              actions: actions,
              focused: widget.focused,
              zoomed: zoomed,
              canMove: !widget.forceZoom,
              desk: widget.desk,
              menuOpen: _menuOpen,
              onToggleMenu: () => setState(() => _menuOpen = !_menuOpen),
            ),
            if (widget.tabs.length > 1)
              WindowTabStrip(tabs: widget.tabs, showBarContainer: true),
            Expanded(
              child: Listener(
                behavior: HitTestBehavior.translucent,
                onPointerDown: (_) {
                  if (!widget.focused) actions.focus();
                  if (_menuOpen) setState(() => _menuOpen = false);
                },
                child: RepaintBoundary(child: widget.child),
              ),
            ),
          ],
        );

        final borderColor = w.isPinned
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
                : (widget.focused ? UiShadows.xl(c) : UiShadows.lg(c)),
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
          key: _focusKey,
          child: WindowTestingMenuHost(
            actions: actions,
            open: _menuOpen,
            onDismiss: () => setState(() => _menuOpen = false),
            topOffset: kDesktopTitleBarHeight + 4,
            alignment: Alignment.topRight,
            child: Positioned.fill(
              child: zoomed
                  ? frame
                  : _ResizeHandles(
                      window: w,
                      onFocus: actions.focus,
                      child: frame,
                    ),
            ),
          ),
        );

        final rect = zoomed ? Offset.zero & widget.desk : w.bounds;
        return AnimatedPositioned(
          duration:
              zoomed != WindowFrame._lastZoom.putIfAbsent(w.id, () => zoomed)
              ? UiTokens.of(context).base
              : Duration.zero,
          curve: UiTokens.of(context).ease,
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
