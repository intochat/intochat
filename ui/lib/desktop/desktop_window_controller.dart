import 'dart:async';
import 'dart:math';

import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'desktop_overlays.dart';
import 'window_frame.dart';

/// Reactive desktop window manager backed by [BrainSessionController].
///
/// Owns local [DesktopWindowState] instances, z-order stacking, pinning
/// invariant (pinned windows always sit above unpinned windows), MRU ordering,
/// cascade placement, and auto-arrange — completely outside widget `build()`.
class DesktopWindowController extends ChangeNotifier {
  DesktopWindowController({
    required this._session,
    Size initialDeskSize = const Size(1280, 752),
  }) : _deskSize = initialDeskSize {
    _session.addListener(_onSessionChanged);
    _syncFromSession(notify: false);
  }

  BrainSessionController _session;
  Size _deskSize;
  final Map<String, DesktopWindowState> _windowStates = {};
  final List<String> _zOrder = [];
  String? _lastBrainId;
  String? _lastFocusedViewId;
  bool _showDesktopOnly = false;
  bool _disposed = false;

  BrainSessionController get session => _session;
  Size get deskSize => _deskSize;
  bool get showDesktopOnly => _showDesktopOnly;

  Map<String, DesktopWindowState> get windowStates =>
      Map<String, DesktopWindowState>.unmodifiable(_windowStates);

  List<String> get zOrder => List<String>.unmodifiable(_zOrder);

  DesktopWindowState? stateFor(String windowId) => _windowStates[windowId];

  /// All active windows from the current projection ordered bottom-to-top by
  /// z-order.
  List<DesktopWindowProjection> get orderedWindows {
    final windows = _session.projection?.windows ?? const [];
    if (windows.isEmpty) return const [];
    final byId = {for (final w in windows) w.id: w};
    return [for (final id in _zOrder) ?byId[id]];
  }

  /// Active non-minimized windows (empty when [showDesktopOnly] is enabled).
  List<DesktopWindowProjection> get visibleWindows {
    if (_showDesktopOnly) return const [];
    return [
      for (final w in orderedWindows)
        if (!(_windowStates[w.id]?.minimized ?? false)) w,
    ];
  }

  /// Active windows ordered top-to-bottom (most recently used first).
  List<DesktopWindowProjection> get mruWindows =>
      orderedWindows.reversed.toList(growable: false);

  /// Top-most visible window, or `null` when none are visible.
  DesktopWindowProjection? get topWindow {
    final visible = visibleWindows;
    return visible.isEmpty ? null : visible.last;
  }

  /// Lookup map of active projection windows by ID.
  Map<String, DesktopWindowProjection> get windowsById {
    final windows = _session.projection?.windows ?? const [];
    return {for (final w in windows) w.id: w};
  }

  /// Finds the top-most open window for [appId], if any.
  DesktopWindowProjection? findOpenWindowForApp(String appId) {
    final norm = appId.trim().toLowerCase();
    for (final w in orderedWindows.reversed) {
      if (w.appId.toLowerCase() == norm) return w;
    }
    final windows = _session.projection?.windows ?? const [];
    for (final w in windows.reversed) {
      if (w.appId.toLowerCase() == norm) return w;
    }
    return null;
  }

  /// Builds MRU-ordered summaries for popovers and the Alt+` switcher.
  List<DesktopWindowSummary> windowSummaries(Set<String> pinnedAppIds) => [
    for (final w in mruWindows)
      DesktopWindowSummary(
        windowId: w.id,
        numericId: w.numericId,
        appId: w.appId,
        title: w.title,
        minimized: _windowStates[w.id]?.minimized ?? false,
        isPinned: pinnedAppIds.contains(w.appId.toLowerCase()),
      ),
  ];

  /// Re-binds to a new [BrainSessionController], tearing down local window
  /// geometry if the brain changed.
  void attachSession(BrainSessionController nextSession) {
    if (_disposed || identical(_session, nextSession)) return;
    _session.removeListener(_onSessionChanged);
    _session = nextSession;
    _session.addListener(_onSessionChanged);
    _syncFromSession(notify: true);
  }

  /// Updates the cached desktop viewport size used for cascade placement of
  /// newly opened windows. Does not mutate existing windows or notify.
  void updateDeskSize(Size desk) {
    if (_disposed || desk == _deskSize || desk.width <= 0 || desk.height <= 0) {
      return;
    }
    _deskSize = desk;
  }

  void setShowDesktopOnly(bool value) {
    if (_disposed || _showDesktopOnly == value) return;
    _showDesktopOnly = value;
    notifyListeners();
  }

  void toggleShowDesktop() => setShowDesktopOnly(!_showDesktopOnly);

  void focusWindow(DesktopWindowProjection spec) {
    if (_disposed) return;
    final state = _windowStates[spec.id];
    if (state != null && state.minimized) {
      state.setMinimized(false);
    }
    _showDesktopOnly = false;
    _zOrder.remove(spec.id);
    _zOrder.add(spec.id);
    _normalizeZOrder();
    notifyListeners();
    unawaited(_session.focusWindow(spec.id));
  }

  void focusWindowById(String windowId) {
    final target = windowsById[windowId];
    if (target != null) focusWindow(target);
  }

  void closeWindow(DesktopWindowProjection spec) {
    if (_disposed) return;
    unawaited(_session.closeWindow(spec.id, tabIndex: spec.activeTabIndex));
  }

  void closeWindowById(String windowId) {
    final target = windowsById[windowId];
    if (target != null) closeWindow(target);
  }

  void toggleMinimized(String windowId) {
    if (_disposed) return;
    final st = _windowStates[windowId];
    if (st == null) return;
    st.setMinimized(!st.minimized);
    notifyListeners();
  }

  void togglePinApp(String appId, Set<String> pinnedAppIds) {
    if (_disposed) return;
    _normalizeZOrder();
    notifyListeners();
    if (pinnedAppIds.contains(appId.toLowerCase())) {
      unawaited(_session.unpinApp(appId));
    } else {
      unawaited(_session.pinApp(appId));
    }
  }

  void normalizeZOrder() {
    if (_disposed) return;
    _normalizeZOrder();
    notifyListeners();
  }

  void bumpReloadEpoch({String? windowId, String? appId}) {
    if (_disposed) return;
    var changed = false;
    if (windowId != null && _windowStates.containsKey(windowId)) {
      _windowStates[windowId]!.reloadEpoch++;
      changed = true;
    } else if (appId != null) {
      final norm = appId.trim().toLowerCase();
      for (final st in _windowStates.values) {
        if (st.appId.toLowerCase() == norm) {
          st.reloadEpoch++;
          changed = true;
        }
      }
    }
    if (changed) notifyListeners();
  }

  /// Auto-arranges unpinned, non-minimized windows into a clean grid while
  /// strictly locking pinned windows.
  void autoArrange(Size desk) {
    if (_disposed) return;
    _deskSize = desk;
    final unpinnedStates = <DesktopWindowState>[
      for (final id in _zOrder)
        if (_windowStates[id] != null &&
            !_windowStates[id]!.minimized &&
            !_windowStates[id]!.isPinned)
          _windowStates[id]!,
    ];
    if (unpinnedStates.isEmpty) return;

    final cols = unpinnedStates.length == 1 ? 1 : 2;
    final rows = (unpinnedStates.length / cols).ceil();
    const pad = 24.0;
    const gap = 16.0;
    final cellW = ((desk.width - pad * 2 - gap * (cols - 1)) / cols).clamp(
      360.0,
      960.0,
    );
    final cellH = ((desk.height - pad * 2 - gap * (rows - 1)) / rows).clamp(
      260.0,
      680.0,
    );

    for (var i = 0; i < unpinnedStates.length; i++) {
      final col = i % cols;
      final row = i ~/ cols;
      unpinnedStates[i].setBounds(
        Rect.fromLTWH(
          pad + col * (cellW + gap),
          pad + row * (cellH + gap),
          cellW,
          cellH,
        ),
      );
    }
    _normalizeZOrder();
    notifyListeners();
  }

  void _onSessionChanged() {
    if (_disposed) return;
    _syncFromSession(notify: true);
  }

  void _syncFromSession({required bool notify}) {
    final brainId = _session.brainId;
    if (brainId.isNotEmpty && _lastBrainId != brainId) {
      _clearLocalWindowGeometry();
      _showDesktopOnly = false;
      _lastBrainId = brainId;
    }

    final projection = _session.projection;
    if (projection == null) {
      if (_windowStates.isNotEmpty || _zOrder.isNotEmpty) {
        _clearLocalWindowGeometry();
      }
      if (notify) notifyListeners();
      return;
    }

    final windows = projection.windows;
    for (var i = 0; i < windows.length; i++) {
      final spec = windows[i];
      final existing = _windowStates[spec.id];
      if (existing == null) {
        _windowStates[spec.id] = DesktopWindowState(
          id: spec.numericId,
          appId: spec.appId,
          title: spec.title,
          neuronUri: 'neuron://$brainId/apps/${spec.appId}',
          bounds: _initialWindowBounds(i, _deskSize),
          zoomed: _shouldLaunchMaximized(spec.appId),
        );
        if (!_zOrder.contains(spec.id)) {
          _zOrder.add(spec.id);
        }
      } else {
        existing.title = spec.title;
        if (!_zOrder.contains(spec.id)) {
          _zOrder.add(spec.id);
        }
      }
    }

    final activeIds = {for (final w in windows) w.id};
    _windowStates.removeWhere((id, state) {
      final gone = !activeIds.contains(id);
      if (gone) state.dispose();
      return gone;
    });
    _zOrder.removeWhere((id) => !activeIds.contains(id));

    final focusedViewId = projection.focusedViewId;
    if (focusedViewId != null && focusedViewId != _lastFocusedViewId) {
      _lastFocusedViewId = focusedViewId;
      for (final w in windows) {
        if (w.activeTabId == focusedViewId ||
            w.tabs.any((t) => t.id == focusedViewId)) {
          _zOrder.remove(w.id);
          _zOrder.add(w.id);
          break;
        }
      }
    }
    _normalizeZOrder();
    if (notify) notifyListeners();
  }

  /// Ensures pinned windows (`isPinned == true`) stay above unpinned windows in `_zOrder`.
  void _normalizeZOrder() {
    final seen = <String>{};
    final unpinned = <String>[];
    final pinned = <String>[];
    for (final id in _zOrder) {
      if (!seen.add(id)) continue;
      if (_windowStates[id]?.isPinned ?? false) {
        pinned.add(id);
      } else {
        unpinned.add(id);
      }
    }
    _zOrder
      ..clear()
      ..addAll(unpinned)
      ..addAll(pinned);
  }

  Rect _initialWindowBounds(int index, Size desk) {
    final w = (desk.width * 0.64).clamp(680.0, 980.0);
    final h = (desk.height * 0.78).clamp(460.0, 680.0);
    final baseLeft = desk.width > 1180
        ? (desk.width - w - 36).clamp(380.0, desk.width - w - 24)
        : ((desk.width - w) / 2).clamp(16.0, max(16.0, desk.width - w - 16));
    final baseTop = ((desk.height - h) / 2 - 12).clamp(
      18.0,
      max(18.0, desk.height - h - 18),
    );
    final offset = (index % 5) * 28.0;
    return Rect.fromLTWH(
      (baseLeft - offset).clamp(16.0, max(16.0, desk.width - w - 16)),
      (baseTop + offset).clamp(16.0, max(16.0, desk.height - h - 16)),
      w,
      h,
    );
  }

  bool _shouldLaunchMaximized(String appId) =>
      _session.catalogById[appId.trim().toLowerCase()]?.launch ==
      AppLaunch.maximized;

  void _clearLocalWindowGeometry() {
    for (final state in _windowStates.values) {
      state.dispose();
    }
    _windowStates.clear();
    _zOrder.clear();
    _lastFocusedViewId = null;
  }

  @override
  void dispose() {
    if (_disposed) return;
    _disposed = true;
    _session.removeListener(_onSessionChanged);
    _clearLocalWindowGeometry();
    super.dispose();
  }
}
