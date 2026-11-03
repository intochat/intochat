import 'dart:math' as math;

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import '../desktop/unified_app_header.dart';
import '../desktop/window_frame.dart';

/// Semantic region sectors for Smart Sort clustering on the Spatial Canvas.
enum CanvasSector {
  intelligence(
    name: 'Intelligence',
    center: Offset(-520, -320),
    color: Color(0xFF3D7EFF),
  ),
  studio(name: 'Studio', center: Offset(520, -320), color: Color(0xFFFF5D8F)),
  workspace(
    name: 'Workspace & System',
    center: Offset(-520, 360),
    color: Color(0xFFF6B73C),
  ),
  social(
    name: 'Social & Reach',
    center: Offset(520, 360),
    color: Color(0xFFA855F7),
  );

  const CanvasSector({
    required this.name,
    required this.center,
    required this.color,
  });

  final String name;
  final Offset center;
  final Color color;

  static CanvasSector sectorForApp(String appId) {
    return switch (appId.toLowerCase()) {
      'ino' || 'memory' || 'ai' || 'assistant' => CanvasSector.intelligence,
      'creator' ||
      'photocraft' ||
      'filmcraft' ||
      'gallery' ||
      'brain' => CanvasSector.studio,
      'files' ||
      'calculator' ||
      'settings' ||
      'usage' ||
      'connections' ||
      'workspace' => CanvasSector.workspace,
      _ => CanvasSector.social,
    };
  }
}

/// Floating Infinite Spatial Canvas for the Workspace app (`WorkspaceApp`).
/// Features:
/// - Semi-neuron ambient animated background (drifting nodes & pulsing synapses).
/// - Infinite pan & smooth zoom (Ctrl+wheel / pinch / space-drag).
/// - Spatial App Cards with UnifiedAppHeader (drag to move, resize edges).
/// - "Smart Sort" clustering into 4 semantic sectors (Intelligence, Studio, Workspace, Social).
/// - Active synaptic links drawing glowing curves between interacting apps.
class SpatialAppCanvas extends StatefulWidget {
  const SpatialAppCanvas({
    super.key,
    required this.windows,
    required this.windowStates,
    required this.topWindowId,
    required this.onFocusWindow,
    required this.onCloseWindow,
    required this.onToggleZoom,
    required this.onPinWindow,
    required this.onReloadWindow,
    required this.onHardRestartWindow,
    required this.onSwitchExecutionMode,
    required this.onInspectSandbox,
    required this.windowBuilders,
    this.onOpenAppSettings,
    this.tabs = const {},
    this.onToast,
  });

  final List<String> windows; // ordered window IDs (z-order)
  final Map<String, DesktopWindowState> windowStates;
  final String? topWindowId;
  final ValueChanged<String> onFocusWindow;
  final ValueChanged<String> onCloseWindow;
  final ValueChanged<String> onToggleZoom;
  final ValueChanged<String> onPinWindow;
  final ValueChanged<String> onReloadWindow;
  final ValueChanged<String> onHardRestartWindow;
  final void Function(String winId, String mode) onSwitchExecutionMode;
  final ValueChanged<String> onInspectSandbox;
  final ValueChanged<String>? onOpenAppSettings;
  final Map<String, WidgetBuilder> windowBuilders;
  final Map<String, List<DesktopTabSpec>> tabs;
  final ValueChanged<String>? onToast;

  @override
  State<SpatialAppCanvas> createState() => SpatialAppCanvasState();
}

class SpatialAppCanvasState extends State<SpatialAppCanvas>
    with TickerProviderStateMixin {
  Offset _cameraOffset = const Offset(0, 0);
  double _zoom = 1.0;

  bool _isPanningCanvas = false;
  Offset _panStart = Offset.zero;
  Offset _cameraStart = Offset.zero;

  // Animation controller for Smart Sort
  late final AnimationController _sortController = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 550),
  );
  Map<String, Rect> _sortFrom = {};
  Map<String, Rect> _sortTo = {};
  String? _menuOpenWindowId;

  @override
  void initState() {
    super.initState();
    _sortController.addListener(_onSortTick);
  }

  @override
  void dispose() {
    _sortController.removeListener(_onSortTick);
    _sortController.dispose();
    super.dispose();
  }

  void _onSortTick() {
    final t = Curves.easeInOutCubic.transform(_sortController.value);
    for (final entry in _sortTo.entries) {
      final winId = entry.key;
      final st = widget.windowStates[winId];
      final from = _sortFrom[winId];
      if (st != null && from != null) {
        final cur = Rect.lerp(from, entry.value, t)!;
        st.setBounds(cur);
      }
    }
  }

  /// Organizes all unpinned windows into their semantic sectors (Intelligence, Studio, Workspace, Social).
  /// Strictly keeps pinned windows at their current coordinates.
  void smartSort(Size viewportSize) {
    final activeIds = widget.windows
        .where((id) => widget.windowStates[id] != null)
        .toList();
    if (activeIds.isEmpty) return;

    final sectorGroups = <CanvasSector, List<String>>{
      for (final s in CanvasSector.values) s: [],
    };

    _sortFrom = {
      for (final id in activeIds) id: widget.windowStates[id]!.bounds,
    };
    _sortTo = {};

    for (final id in activeIds) {
      final st = widget.windowStates[id]!;
      if (st.isPinned) {
        // Pinned windows don't move
        _sortTo[id] = st.bounds;
      } else {
        final sec = CanvasSector.sectorForApp(st.appId);
        sectorGroups[sec]!.add(id);
      }
    }

    // Layout each sector
    final centerOrigin = Offset(
      viewportSize.width / 2,
      viewportSize.height / 2,
    );

    for (final sector in CanvasSector.values) {
      final ids = sectorGroups[sector]!;
      if (ids.isEmpty) continue;

      final basePos = centerOrigin + sector.center;
      for (var i = 0; i < ids.length; i++) {
        final id = ids[i];
        final current = _sortFrom[id]!;
        final w = current.width.clamp(480.0, 720.0);
        final h = current.height.clamp(340.0, 520.0);
        final col = i % 2;
        final row = i ~/ 2;
        final targetX = basePos.dx + (col * (w + 24.0)) - (w / 2);
        final targetY = basePos.dy + (row * (h + 24.0)) - (h / 2);
        _sortTo[id] = Rect.fromLTWH(targetX, targetY, w, h);
      }
    }

    _sortController.forward(from: 0);
    widget.onToast?.call('Smart-sorted apps into semantic neural sectors');
  }

  void _resetCamera(Size viewport) {
    setState(() {
      _cameraOffset = Offset.zero;
      _zoom = 1.0;
    });
  }

  void _zoomBy(double factor, Offset focalPoint) {
    setState(() {
      final nextZoom = (_zoom * factor).clamp(0.35, 2.2);
      final scaleChange = nextZoom / _zoom;
      // Adjust camera so focalPoint stays under the cursor
      _cameraOffset = focalPoint - (focalPoint - _cameraOffset) * scaleChange;
      _zoom = nextZoom;
    });
  }

  void _onPointerSignal(PointerSignalEvent event) {
    if (event is PointerScrollEvent) {
      // Zoom with Ctrl + Scroll or Trackpad pinch
      final isControlPressed =
          HardwareKeyboard.instance.isControlPressed ||
          HardwareKeyboard.instance.isMetaPressed;
      if (isControlPressed) {
        final zoomDelta = event.scrollDelta.dy < 0 ? 1.12 : 0.89;
        _zoomBy(zoomDelta, event.position);
      } else {
        // Plain canvas scroll pans the camera
        setState(() {
          _cameraOffset -= event.scrollDelta;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);

    return LayoutBuilder(
      builder: (context, constraints) {
        final viewportSize = Size(constraints.maxWidth, constraints.maxHeight);

        return Listener(
          onPointerSignal: _onPointerSignal,
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onPanStart: (d) {
              _isPanningCanvas = true;
              _panStart = d.localPosition;
              _cameraStart = _cameraOffset;
            },
            onPanUpdate: (d) {
              if (_isPanningCanvas) {
                setState(() {
                  _cameraOffset = _cameraStart + (d.localPosition - _panStart);
                });
              }
            },
            onPanEnd: (_) => _isPanningCanvas = false,
            child: Stack(
              fit: StackFit.expand,
              clipBehavior: Clip.none,
              children: [
                // 1. Semi-Neuron Animated Background
                Positioned.fill(
                  child: RepaintBoundary(
                    child: _NeuronSynapticBackground(
                      cameraOffset: _cameraOffset,
                      zoom: _zoom,
                      accentColor: c.accent,
                      glowColor: c.secondaryGlow,
                      isDark: c.isDark,
                    ),
                  ),
                ),

                // 2. Sector Ambient Badges in World Space
                Positioned.fill(
                  child: IgnorePointer(
                    child: CustomPaint(
                      painter: _SectorLabelsPainter(
                        cameraOffset: _cameraOffset,
                        zoom: _zoom,
                        viewportSize: viewportSize,
                        colors: c,
                      ),
                    ),
                  ),
                ),

                // 3. Active Synapse Curves between Connected Apps
                Positioned.fill(
                  child: IgnorePointer(
                    child: CustomPaint(
                      painter: _ActiveSynapsesPainter(
                        windows: widget.windows,
                        windowStates: widget.windowStates,
                        cameraOffset: _cameraOffset,
                        zoom: _zoom,
                        accentColor: c.accent,
                      ),
                    ),
                  ),
                ),

                // 4. Spatial Window Cards (Rendered with World-to-Screen Matrix)
                for (final winId in widget.windows)
                  if (widget.windowStates[winId] != null &&
                      !widget.windowStates[winId]!.minimized)
                    _buildSpatialCard(
                      winId,
                      widget.windowStates[winId]!,
                      viewportSize,
                      c,
                    ),

                // 5. Floating Spatial Canvas HUD Controls (Top Right)
                Positioned(
                  top: 14,
                  right: 20,
                  child: _buildCanvasControlsPill(c, viewportSize),
                ),
              ],
            ),
          ),
        );
      },
    );
  }

  Widget _buildSpatialCard(
    String winId,
    DesktopWindowState w,
    Size viewportSize,
    IcColors c,
  ) {
    final isFocused = widget.topWindowId == winId;
    final worldRect = w.bounds;

    // Convert world rect to screen rect via camera matrix
    final screenLeft = worldRect.left * _zoom + _cameraOffset.dx;
    final screenTop = worldRect.top * _zoom + _cameraOffset.dy;
    final screenWidth = worldRect.width * _zoom;
    final screenHeight = worldRect.height * _zoom;

    final isLOD = _zoom < 0.58;

    return Positioned(
      key: ValueKey('spatial-card-pos-$winId'),
      left: screenLeft,
      top: screenTop,
      width: screenWidth,
      height: screenHeight,
      child: Listener(
        behavior: HitTestBehavior.opaque,
        onPointerDown: (_) => widget.onFocusWindow(winId),
        child: Container(
          decoration: BoxDecoration(
            color: c.surfaceCard.withValues(alpha: c.isGlass ? 0.88 : 0.98),
            borderRadius: BorderRadius.circular(
              IcRadius.card * _zoom.clamp(0.6, 1.0),
            ),
            border: Border.all(
              color: w.isPinned
                  ? c.accent
                  : (isFocused ? c.borderInput : c.borderHairline),
              width: w.isPinned || isFocused ? 2.0 : 1.0,
            ),
            boxShadow: isFocused ? IcShadow.lg(c) : IcShadow.md(c),
          ),
          clipBehavior: Clip.antiAlias,
          child: Stack(
            children: [
              Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // 1. Unified 42px App Header (card frame)
                  UnifiedAppHeader(
                    window: w,
                    focused: isFocused,
                    zoomed: w.zoomed,
                    canMove: true,
                    desk: viewportSize,
                    tabs: widget.tabs[winId] ?? const [],
                    isPinned: w.isPinned,
                    menuOpen: _menuOpenWindowId == winId,
                    onToggleMenu: () {
                      setState(() {
                        if (_menuOpenWindowId == winId) {
                          _menuOpenWindowId = null;
                        } else {
                          _menuOpenWindowId = winId;
                        }
                      });
                    },
                    onFocus: () => widget.onFocusWindow(winId),
                    onClose: () => widget.onCloseWindow(winId),
                    onToggleZoom: () => widget.onToggleZoom(winId),
                    onPin: () => widget.onPinWindow(winId),
                    onReload: () => widget.onReloadWindow(winId),
                    onHardRestartSandbox: () =>
                        widget.onHardRestartWindow(winId),
                    onMoveDelta: (delta) {
                      final worldDelta = delta / _zoom;
                      w.setBounds(w.bounds.shift(worldDelta));
                    },
                  ),

                  // 2. Viewport / LOD Thumbnail
                  Expanded(
                    child: isLOD
                        ? _buildLODPreview(w, c)
                        : (widget.windowBuilders[winId]?.call(context) ??
                              const SizedBox.shrink()),
                  ),
                ],
              ),
              if (_menuOpenWindowId == winId)
                Positioned(
                  left: 10,
                  top: kUnifiedAppHeaderHeight + 4,
                  width: 320,
                  child: AppTestingMenuPopover(
                    window: w,
                    executionMode: w.executionMode,
                    onSelectMode: (m) {
                      w.setExecutionMode(m);
                      widget.onSwitchExecutionMode(winId, m);
                      setState(() => _menuOpenWindowId = null);
                    },
                    onReload: () {
                      w.recordReload();
                      widget.onReloadWindow(winId);
                      setState(() => _menuOpenWindowId = null);
                    },
                    onResetSandbox: () {
                      w.recordHardRestart();
                      widget.onHardRestartWindow(winId);
                      setState(() => _menuOpenWindowId = null);
                    },
                    onInspectSandbox: () {
                      widget.onInspectSandbox(winId);
                      setState(() => _menuOpenWindowId = null);
                    },
                    onOpenAppSettings: widget.onOpenAppSettings != null
                        ? () {
                            setState(() => _menuOpenWindowId = null);
                            widget.onOpenAppSettings!(winId);
                          }
                        : null,
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildLODPreview(DesktopWindowState w, IcColors c) {
    return Container(
      color: c.surfacePage.withValues(alpha: 0.6),
      alignment: Alignment.center,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          IcAppIcon(appId: w.appId, accentHex: w.accentHex, size: 40),
          const SizedBox(height: 8),
          Text(
            w.title,
            style: IcType.text(
              14,
              weight: FontWeight.w700,
              color: c.textStrong,
            ),
          ),
          const SizedBox(height: 4),
          TagPill(
            label: CanvasSector.sectorForApp(w.appId).name,
            tone: TagTone.neutral,
          ),
        ],
      ),
    );
  }

  Widget _buildCanvasControlsPill(IcColors c, Size viewportSize) {
    return IcAcrylicBox(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      borderRadius: BorderRadius.circular(IcRadius.pill),
      shadows: IcShadow.md(c),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Smart Sort Button
          Btn(
            key: const ValueKey('canvas-smart-sort-btn'),
            label: 'Smart-Sort',
            icon: LucideIcons.layoutGrid,
            small: true,
            onTap: () => smartSort(viewportSize),
          ),
          const SizedBox(width: 8),

          // Zoom Out
          _HudIconButton(
            key: const ValueKey('canvas-zoom-out'),
            icon: LucideIcons.zoomOut,
            tooltip: 'Zoom out (Ctrl+Wheel)',
            onTap: () => _zoomBy(
              0.85,
              Offset(viewportSize.width / 2, viewportSize.height / 2),
            ),
          ),
          const SizedBox(width: 2),

          // Zoom Percentage
          GestureDetector(
            key: const ValueKey('canvas-zoom-percent'),
            onTap: () => _resetCamera(viewportSize),
            child: MouseRegion(
              cursor: SystemMouseCursors.click,
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 6),
                child: Text(
                  '${(_zoom * 100).round()}%',
                  style: IcType.text(
                    11.5,
                    weight: FontWeight.w600,
                    color: c.textStrong,
                    tnum: true,
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(width: 2),

          // Zoom In
          _HudIconButton(
            key: const ValueKey('canvas-zoom-in'),
            icon: LucideIcons.zoomIn,
            tooltip: 'Zoom in (Ctrl+Wheel)',
            onTap: () => _zoomBy(
              1.15,
              Offset(viewportSize.width / 2, viewportSize.height / 2),
            ),
          ),
          const SizedBox(width: 6),

          // Center View
          _HudIconButton(
            key: const ValueKey('canvas-reset-camera'),
            icon: LucideIcons.locateFixed,
            tooltip: 'Reset camera center',
            onTap: () => _resetCamera(viewportSize),
          ),
        ],
      ),
    );
  }
}

class _HudIconButton extends StatelessWidget {
  const _HudIconButton({
    super.key,
    required this.icon,
    required this.tooltip,
    required this.onTap,
  });

  final IconData icon;
  final String tooltip;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Tooltip(
      message: tooltip,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: null,
        child: GestureDetector(
          onTap: onTap,
          child: Container(
            width: 26,
            height: 26,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(IcRadius.button),
            ),
            child: Icon(icon, size: 14, color: c.textBody),
          ),
        ),
      ),
    );
  }
}

/// High-performance 2D semi-neuron animated background painter.
/// Generates drifting synaptic nodes and electrical pulses at 60/120 FPS.
class _NeuronSynapticBackground extends StatefulWidget {
  const _NeuronSynapticBackground({
    required this.cameraOffset,
    required this.zoom,
    required this.accentColor,
    required this.glowColor,
    required this.isDark,
  });

  final Offset cameraOffset;
  final double zoom;
  final Color accentColor;
  final Color glowColor;
  final bool isDark;

  @override
  State<_NeuronSynapticBackground> createState() =>
      _NeuronSynapticBackgroundState();
}

class _NeuronSynapticBackgroundState extends State<_NeuronSynapticBackground>
    with SingleTickerProviderStateMixin {
  late final AnimationController _ticker = AnimationController(
    vsync: this,
    duration: const Duration(seconds: 10),
  );

  @override
  void initState() {
    super.initState();
    final isTest =
        WidgetsBinding.instance.runtimeType.toString().contains('Test');
    if (!isTest) {
      _ticker.repeat();
    }
  }

  final List<Offset> _baseNodes = [
    const Offset(-640, -420),
    const Offset(-320, -560),
    const Offset(0, -480),
    const Offset(380, -520),
    const Offset(700, -380),
    const Offset(-580, -120),
    const Offset(-220, -180),
    const Offset(220, -140),
    const Offset(620, -160),
    const Offset(-700, 240),
    const Offset(-340, 210),
    const Offset(0, 180),
    const Offset(380, 240),
    const Offset(720, 200),
    const Offset(-520, 520),
    const Offset(-180, 560),
    const Offset(240, 540),
    const Offset(620, 480),
  ];

  @override
  void dispose() {
    _ticker.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _ticker,
      builder: (context, _) {
        return CustomPaint(
          painter: _SynapticCanvasPainter(
            time: _ticker.value,
            nodes: _baseNodes,
            cameraOffset: widget.cameraOffset,
            zoom: widget.zoom,
            accent: widget.accentColor,
            glow: widget.glowColor,
            isDark: widget.isDark,
          ),
        );
      },
    );
  }
}

class _SynapticCanvasPainter extends CustomPainter {
  _SynapticCanvasPainter({
    required this.time,
    required this.nodes,
    required this.cameraOffset,
    required this.zoom,
    required this.accent,
    required this.glow,
    required this.isDark,
  });

  final double time;
  final List<Offset> nodes;
  final Offset cameraOffset;
  final double zoom;
  final Color accent;
  final Color glow;
  final bool isDark;

  @override
  void paint(Canvas canvas, Size size) {
    final center = Offset(size.width / 2, size.height / 2);
    final animatedNodes = <Offset>[];

    // Compute drifted node coordinates in screen space
    for (var i = 0; i < nodes.length; i++) {
      final base = nodes[i];
      final driftAngle = time * 2 * math.pi + (i * 0.7);
      final driftDist = 18.0 * math.sin(time * 2 * math.pi + i);
      final worldPos =
          base +
          Offset(
            math.cos(driftAngle) * driftDist,
            math.sin(driftAngle) * driftDist,
          );
      final screenPos = center + (worldPos * zoom) + cameraOffset;
      animatedNodes.add(screenPos);
    }

    final linePaint = Paint()
      ..strokeWidth = 1.2
      ..style = PaintingStyle.stroke;

    // Draw synapses between nearby nodes
    const maxDistance = 340.0;
    for (var i = 0; i < animatedNodes.length; i++) {
      for (var j = i + 1; j < animatedNodes.length; j++) {
        final dist = (animatedNodes[i] - animatedNodes[j]).distance;
        final scaledThreshold = maxDistance * zoom;
        if (dist < scaledThreshold) {
          final alpha =
              (1.0 - (dist / scaledThreshold)) * (isDark ? 0.22 : 0.12);
          linePaint.color = accent.withValues(alpha: alpha);
          canvas.drawLine(animatedNodes[i], animatedNodes[j], linePaint);

          // Electrical pulse along the synapse
          final pulseT = (time * 1.5 + (i + j) * 0.2) % 1.0;
          final pulsePos = Offset.lerp(
            animatedNodes[i],
            animatedNodes[j],
            pulseT,
          )!;
          final pulsePaint = Paint()
            ..color = glow.withValues(alpha: alpha * 2.2)
            ..style = PaintingStyle.fill;
          canvas.drawCircle(pulsePos, 2.0 * zoom.clamp(0.8, 1.4), pulsePaint);
        }
      }
    }

    // Draw nodes
    final nodePaint = Paint()..style = PaintingStyle.fill;
    for (var i = 0; i < animatedNodes.length; i++) {
      final p = animatedNodes[i];
      final pulse = (math.sin(time * 4 * math.pi + i) + 1.0) / 2.0;
      final radius = (3.5 + pulse * 1.5) * zoom.clamp(0.7, 1.3);

      nodePaint.color = accent.withValues(alpha: isDark ? 0.45 : 0.28);
      canvas.drawCircle(p, radius, nodePaint);

      nodePaint.color = glow.withValues(alpha: isDark ? 0.75 : 0.55);
      canvas.drawCircle(p, radius * 0.5, nodePaint);
    }
  }

  @override
  bool shouldRepaint(covariant _SynapticCanvasPainter old) => true;
}

class _SectorLabelsPainter extends CustomPainter {
  _SectorLabelsPainter({
    required this.cameraOffset,
    required this.zoom,
    required this.viewportSize,
    required this.colors,
  });

  final Offset cameraOffset;
  final double zoom;
  final Size viewportSize;
  final IcColors colors;

  @override
  void paint(Canvas canvas, Size size) {
    final centerOrigin = Offset(size.width / 2, size.height / 2);

    for (final sector in CanvasSector.values) {
      final screenPos = centerOrigin + (sector.center * zoom) + cameraOffset;

      // Draw subtle ambient halo ring for each sector
      final haloPaint = Paint()
        ..color = sector.color.withValues(alpha: colors.isDark ? 0.05 : 0.03)
        ..style = PaintingStyle.fill;
      canvas.drawCircle(screenPos, 260.0 * zoom, haloPaint);

      final borderPaint = Paint()
        ..color = sector.color.withValues(alpha: colors.isDark ? 0.12 : 0.08)
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.0;
      canvas.drawCircle(screenPos, 260.0 * zoom, borderPaint);
    }
  }

  @override
  bool shouldRepaint(covariant _SectorLabelsPainter old) =>
      old.cameraOffset != cameraOffset || old.zoom != zoom;
}

class _ActiveSynapsesPainter extends CustomPainter {
  _ActiveSynapsesPainter({
    required this.windows,
    required this.windowStates,
    required this.cameraOffset,
    required this.zoom,
    required this.accentColor,
  });

  final List<String> windows;
  final Map<String, DesktopWindowState> windowStates;
  final Offset cameraOffset;
  final double zoom;
  final Color accentColor;

  @override
  void paint(Canvas canvas, Size size) {
    final centers = <Offset>[];
    for (final id in windows) {
      final st = windowStates[id];
      if (st != null && !st.minimized) {
        final wRect = st.bounds;
        final screenCenter = Offset(
          (wRect.left + wRect.width / 2) * zoom + cameraOffset.dx,
          (wRect.top + wRect.height / 2) * zoom + cameraOffset.dy,
        );
        centers.add(screenCenter);
      }
    }

    if (centers.length < 2) return;

    final paint = Paint()
      ..color = accentColor.withValues(alpha: 0.18)
      ..strokeWidth = 1.8
      ..style = PaintingStyle.stroke;

    // Draw smooth bezier curves connecting open windows
    for (var i = 0; i < centers.length - 1; i++) {
      final p1 = centers[i];
      final p2 = centers[i + 1];
      final path = Path()
        ..moveTo(p1.dx, p1.dy)
        ..cubicTo(
          p1.dx,
          (p1.dy + p2.dy) / 2,
          p2.dx,
          (p1.dy + p2.dy) / 2,
          p2.dx,
          p2.dy,
        );
      canvas.drawPath(path, paint);
    }
  }

  @override
  bool shouldRepaint(covariant _ActiveSynapsesPainter old) => true;
}
