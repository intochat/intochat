import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'cross_window_router.dart';

/// Translates a legacy `{kind: settings, brainId?, tab?}` wire map through the
/// single [ShellActivation.fromWireMap] ingress boundary into the canonical
/// Settings workspace activation wire map.
Map<String, dynamic> settingsActivation(Map<String, dynamic> action) =>
    ShellActivation.fromWireMap(action).toWireMap();

/// Snapshot of shell routing state read by [ShellActivationRouter] for each
/// activation.
@immutable
class ShellRoutingState {
  const ShellRoutingState({
    required this.currentBrain,
    this.isDesktopRoot = false,
    this.hasExplicitRoot = false,
    this.activationApp,
    this.rootName,
    this.focusedNeuron,
  });

  final String? currentBrain;
  final bool isDesktopRoot;
  final bool hasExplicitRoot;
  final String? activationApp;
  final String? rootName;
  final String? focusedNeuron;
}

/// Desktop window-manager hooks attached to [ShellActivationRouter] while
/// `LiveDesktopShell` is mounted.
class DesktopActivationDelegate {
  const DesktopActivationDelegate({
    required this.findOpenWindow,
    required this.focusWindow,
    required this.openApp,
    required this.onShowDesktopCleared,
    required this.onWindowNavigated,
    required this.requestShellOperation,
    this.onNotifyStandaloneActivation,
  });

  final DesktopWindowProjection? Function(String appId) findOpenWindow;
  final void Function(DesktopWindowProjection window) focusWindow;
  final Future<void> Function(String appId) openApp;
  final VoidCallback onShowDesktopCleared;
  final void Function({String? windowId, String? appId}) onWindowNavigated;
  final Future<void> Function(String app, String operation, String input)
  requestShellOperation;

  /// Optional callback used ONLY when `LiveDesktopShell` runs standalone
  /// (outside `WorkspaceApp`) so tests observing `BrainSessionController.onActivate`
  /// receive the normalized [ShellActivation] without re-entering the router.
  final ValueChanged<ShellActivation>? onNotifyStandaloneActivation;
}

/// Outcome of routing an activation through [ShellActivationRouter].
@immutable
class ShellActivationResult {
  const ShellActivationResult({
    this.handled = false,
    this.refusedByDepartureGuard = false,
    this.refusalMessage,
    this.dispatchedNavigateTo = false,
    this.focusedExistingWindow = false,
    this.openedNewWindow = false,
  });

  final bool handled;
  final bool refusedByDepartureGuard;
  final String? refusalMessage;
  final bool dispatchedNavigateTo;
  final bool focusedExistingWindow;
  final bool openedNewWindow;
}

/// Single typed activation and cross-window navigation router for both
/// `WorkspaceApp` and `LiveDesktopShell`.
///
/// Owns `os_uri` resolution, `ui.navigate-from` departure-guard checks,
/// single-dispatch `ui.navigate-to` delivery, brain switching, and desktop
/// window open/focus coordination.
class ShellActivationRouter {
  ShellActivationRouter({
    required this.readState,
    required this.sendAct,
    this.requestApp,
    this.listBrainIds,
    this.selectBrain,
    this.onSwitchAccount,
    this.onEndLobby,
    this.onReconnect,
    this.onAppLifecycle,
    this.onOpenConnections,
    this.onShowNeuron,
    this.onSwitchExplicitSurface,
    this.onDepartureRefused,
    this.onRegisterKnownApp,
    this.onToast,
    this.isMounted,
    this._desktopDelegate,
  });

  final ShellRoutingState Function() readState;
  final Future<Object?> Function(String brain, UiAction action) sendAct;
  final Future<void> Function(
    String brain,
    String app,
    String operation,
    String input,
  )?
  requestApp;
  final Future<List<String>> Function()? listBrainIds;
  final Future<void> Function(String brain, {String? show})? selectBrain;
  final Future<void> Function()? onSwitchAccount;
  final Future<void> Function()? onEndLobby;
  final Future<void> Function(ReconnectActivation activation)? onReconnect;
  final Future<void> Function(String brain, AppLifecycleActivation activation)?
  onAppLifecycle;
  final void Function(String brain, ConnectionsActivation activation)?
  onOpenConnections;
  final Future<void> Function(String brain, String neuron)? onShowNeuron;
  final void Function(String brain, String app, String neuron)?
  onSwitchExplicitSurface;
  final void Function(String refusal)? onDepartureRefused;
  final void Function(String appId)? onRegisterKnownApp;
  final void Function(String message, {UiTone tone})? onToast;
  final bool Function()? isMounted;

  DesktopActivationDelegate? _desktopDelegate;

  bool get _mounted => isMounted?.call() ?? true;

  void attachDesktop(DesktopActivationDelegate delegate) {
    _desktopDelegate = delegate;
  }

  void detachDesktop(DesktopActivationDelegate delegate) {
    if (identical(_desktopDelegate, delegate)) {
      _desktopDelegate = null;
    }
  }

  static String? _nonEmptyField(Object? value) {
    if (value is! String) return null;
    final trimmed = value.trim();
    return trimmed.isEmpty ? null : value;
  }

  /// Resolves an [OpenWindowActivation] (`os_uri`) into a normalized
  /// [WorkspaceAppActivation]. Returns `null` when the URI is invalid or
  /// unrecognized.
  static WorkspaceAppActivation? resolveOpenWindow(
    OpenWindowActivation activation, {
    String? fallbackFromNeuron,
  }) {
    final openRoute = openWindowRouteOf(
      activation.uri,
      intent: activation.intent,
      fallbackFromNeuron: fallbackFromNeuron,
    );
    if (openRoute != null) {
      if (openRoute.app == 'settings' && !openRoute.hasNavigationPayload) {
        return WorkspaceAppActivation.settings(
          brainId: activation.brainId,
          tab: openRoute.section ?? '',
          section: openRoute.section,
        );
      }
      return WorkspaceAppActivation(
        application: openRoute.app,
        brainId: activation.brainId,
        operation: 'open',
        input: openRoute.input,
        argsJson: openRoute.argsJson,
        rawTransition: openRoute.hasExplicitTransition
            ? openRoute.rawTransition
            : null,
        fromNeuron: openRoute.fromNeuron,
        settingsSection: openRoute.section,
        hasNavigationPayload: openRoute.hasNavigationPayload,
      );
    }
    if (activation.uri.contains('settings')) {
      final uri = Uri.tryParse(activation.uri);
      final section = _nonEmptyField(uri?.queryParameters['section']);
      return WorkspaceAppActivation.settings(
        brainId: activation.brainId,
        tab: section ?? '',
        section: section,
      );
    }
    return null;
  }

  /// Launches or focuses [appId] through the unified activation pipeline.
  Future<ShellActivationResult> launchOrFocusApp(
    String appId, {
    String? brainId,
    String? settingsSection,
    String? openInput,
    OpenWindowRoute? openRoute,
    String operation = 'open',
    Map<String, dynamic>? extra,
  }) {
    final effectiveBrain = brainId ?? readState().currentBrain;
    final effectiveInput =
        openRoute?.input ??
        (openInput != null && openInput.isNotEmpty ? openInput : null);
    final argsJson = openRoute?.argsJson;
    final hasExplicitTransition = openRoute?.hasExplicitTransition ?? false;
    final rawTransition = openRoute?.rawTransition;
    final fromNeuron = openRoute?.fromNeuron;
    final hasNavPayload =
        effectiveInput != null || argsJson != null || hasExplicitTransition;

    if (appId == 'settings' && !hasNavPayload && operation == 'open') {
      return activate(
        WorkspaceAppActivation.settings(
          brainId: effectiveBrain,
          tab: settingsSection ?? '',
          section: settingsSection,
        ),
      );
    }

    return activate(
      WorkspaceAppActivation(
        application: appId,
        brainId: effectiveBrain,
        operation: operation,
        input: effectiveInput,
        argsJson: argsJson,
        rawTransition: hasExplicitTransition ? rawTransition : null,
        fromNeuron: fromNeuron,
        hasNavigationPayload: hasNavPayload,
        extra: extra ?? const {},
      ),
    );
  }

  /// Boundary wire ingress: parses a raw activation map via
  /// [ShellActivation.tryFromWireMap] and routes it through [activate].
  Future<ShellActivationResult> activateWire(
    Map<String, dynamic> rawAction, {
    String? sourceAppId,
  }) async {
    final activation = ShellActivation.tryFromWireMap(rawAction);
    if (activation == null) return const ShellActivationResult();
    return activate(activation, sourceAppId: sourceAppId);
  }

  /// Routes any typed [ShellActivation] through one unified, single-dispatch
  /// state machine.
  Future<ShellActivationResult> activate(
    ShellActivation activation, {
    String? sourceAppId,
  }) async {
    final state = readState();
    final fallbackFrom =
        sourceAppId != null &&
            sourceAppId.isNotEmpty &&
            state.currentBrain != null
        ? '${state.currentBrain}/apps/$sourceAppId'
        : screenNeuronFromViewName(
            state.rootName ?? state.focusedNeuron,
            brain: state.currentBrain,
          );

    try {
      switch (activation) {
        case AccountActivation():
          await onSwitchAccount?.call();
          return const ShellActivationResult(handled: true);

        case LobbyEndedActivation():
          await onEndLobby?.call();
          return const ShellActivationResult(handled: true);

        case ReconnectActivation():
          if (activation.application == 'desktop' &&
              state.currentBrain != null) {
            await onReconnect?.call(activation);
          }
          return const ShellActivationResult(handled: true);

        case AppLifecycleActivation():
          final brain = activation.brainId ?? state.currentBrain;
          if (brain == null || brain.isEmpty) {
            return const ShellActivationResult();
          }
          if (brain != state.currentBrain && listBrainIds != null) {
            final brains = await listBrainIds!();
            if (!brains.contains(brain)) {
              throw StateError('This account is not a member of that brain.');
            }
          }
          await onAppLifecycle?.call(brain, activation);
          return const ShellActivationResult(handled: true);

        case ConnectionsActivation():
          final brain = activation.brainId ?? state.currentBrain;
          if (brain == null || brain.isEmpty) {
            return const ShellActivationResult();
          }
          final changingBrain = brain != state.currentBrain;
          if (changingBrain) {
            await _switchBrainIfAllowed(brain, show: null);
            if (!_mounted) return const ShellActivationResult();
          }
          if (_mounted) {
            onOpenConnections?.call(brain, activation);
          }
          return const ShellActivationResult(handled: true);

        case OpenWindowActivation():
          final resolved = resolveOpenWindow(
            activation,
            fallbackFromNeuron: fallbackFrom,
          );
          if (resolved == null) return const ShellActivationResult();
          return await _routeSurfaceActivation(
            state: state,
            activation: resolved,
            brainId: resolved.brainId,
            appId: resolved.application,
            explicitNeuron: resolved.neuron,
            operation: resolved.operation,
            input: resolved.nonEmptyInput,
            rawInput: resolved.input,
            argsJson: resolved.argsJson,
            rawTransition: resolved.rawTransition,
            fromNeuron: resolved.fromNeuron,
            settingsSection: resolved.settingsSection,
            hasNavigationPayload: resolved.hasNavigationPayload,
          );

        case WorkspaceAppActivation():
          return await _routeSurfaceActivation(
            state: state,
            activation: activation,
            brainId: activation.brainId,
            appId: activation.application,
            explicitNeuron: activation.neuron,
            operation: activation.operation,
            input: activation.nonEmptyInput,
            rawInput: activation.input,
            argsJson: activation.argsJson,
            rawTransition: activation.rawTransition,
            fromNeuron: activation.fromNeuron,
            settingsSection: activation.settingsSection,
            hasNavigationPayload: activation.hasNavigationPayload,
          );

        case ShowNeuronActivation():
          return await _routeSurfaceActivation(
            state: state,
            activation: activation,
            brainId: activation.brainId,
            appId: null,
            explicitNeuron: activation.neuron,
            operation: activation.operation,
            input: _nonEmptyField(activation.input),
            rawInput: activation.input,
            argsJson: activation.argsJson,
            rawTransition: activation.rawTransition,
            fromNeuron: activation.fromNeuron,
            settingsSection: null,
            hasNavigationPayload: activation.hasNavigationPayload,
          );
      }
    } on StateError catch (error) {
      if (_mounted) {
        onToast?.call(error.message, tone: UiTone.error);
      }
      return const ShellActivationResult();
    } catch (_) {
      if (_mounted) {
        onToast?.call(
          'Could not navigate there. Try again.',
          tone: UiTone.error,
        );
      }
      return const ShellActivationResult();
    }
  }

  Future<void> _switchBrainIfAllowed(
    String brain, {
    required String? show,
  }) async {
    if (listBrainIds != null) {
      final brains = await listBrainIds!();
      if (!brains.contains(brain)) {
        throw StateError('This account is not a member of that brain.');
      }
    }
    if (!_mounted) return;
    await selectBrain?.call(brain, show: show);
  }

  Future<ShellActivationResult> _routeSurfaceActivation({
    required ShellRoutingState state,
    required ShellActivation activation,
    required String? brainId,
    required String? appId,
    required String? explicitNeuron,
    required String? operation,
    required String? input,
    required String? rawInput,
    required String? argsJson,
    required String? rawTransition,
    required String? fromNeuron,
    required String? settingsSection,
    required bool hasNavigationPayload,
  }) async {
    final brain = brainId ?? state.currentBrain;
    if (brain == null || brain.isEmpty) {
      return const ShellActivationResult();
    }
    final neuron =
        explicitNeuron ?? (appId == null ? null : '$brain/apps/$appId');
    final transition = normalizeViewTransition(rawTransition);
    final changingBrain = brain != state.currentBrain;

    if (changingBrain) {
      await _switchBrainIfAllowed(brain, show: neuron);
      if (!_mounted) return const ShellActivationResult();
    } else if (appId != null && appId.trim().isNotEmpty) {
      onRegisterKnownApp?.call(appId.trim().toLowerCase());
    }

    final latestState = readState();
    final desktop = _desktopDelegate;

    // Desktop root path: coordinate local window manager & single navigate-to.
    if (latestState.isDesktopRoot && desktop != null && appId != null) {
      return await _activateDesktopWindow(
        brain: brain,
        app: appId,
        neuron: neuron,
        changingBrain: changingBrain,
        activation: activation,
        operation: operation,
        input: input,
        rawInput: rawInput,
        argsJson: argsJson,
        transition: transition,
        fromNeuron: fromNeuron,
        settingsSection: settingsSection,
        hasNavigationPayload: hasNavigationPayload,
        state: latestState,
        desktop: desktop,
      );
    }

    // Explicit root without desktop delegate.
    if (latestState.hasExplicitRoot) {
      return await _activateExplicitRoot(
        brain: brain,
        app: appId,
        neuron: neuron,
        changingBrain: changingBrain,
        activation: activation,
        operation: operation,
        input: input,
        rawInput: rawInput,
        argsJson: argsJson,
        transition: transition,
        fromNeuron: fromNeuron,
        hasNavigationPayload: hasNavigationPayload,
        state: latestState,
      );
    }

    // Default workspace path (non-desktop or before desktop attaches).
    final previousScreen = screenNeuronFromViewName(
      latestState.focusedNeuron,
      brain: brain,
    );
    if (!latestState.isDesktopRoot &&
        neuron != null &&
        !changingBrain &&
        previousScreen != null &&
        previousScreen != neuron) {
      final refusal = await checkDepartureGuard(
        sendAct: (event) => sendAct(brain, event),
        fromNeuron: previousScreen,
        toNeuron: neuron,
        route: argsJson ?? input,
      );
      if (refusal != null) {
        if (_mounted) {
          onDepartureRefused?.call(refusal);
          onToast?.call(refusal, tone: UiTone.error);
        }
        return ShellActivationResult(
          handled: true,
          refusedByDepartureGuard: true,
          refusalMessage: refusal,
        );
      }
    }

    if (neuron != null && !changingBrain) {
      await onShowNeuron?.call(brain, neuron);
    }

    var navDelivered = false;
    if (neuron != null && hasNavigationPayload) {
      navDelivered = await dispatchNavigateTo(
        sendAct: (event) => sendAct(brain, event),
        targetNeuron: neuron,
        fromNeuron: fromNeuron ?? previousScreen,
        argsJson: argsJson,
        transition: transition,
        input: input,
      );
    }

    if (operation != null && !navDelivered && appId != null) {
      await requestApp?.call(brain, appId, operation, rawInput ?? '');
    }

    return ShellActivationResult(
      handled: true,
      dispatchedNavigateTo: navDelivered,
    );
  }

  Future<ShellActivationResult> _activateDesktopWindow({
    required String brain,
    required String app,
    required String? neuron,
    required bool changingBrain,
    required ShellActivation activation,
    required String? operation,
    required String? input,
    required String? rawInput,
    required String? argsJson,
    required String transition,
    required String? fromNeuron,
    required String? settingsSection,
    required bool hasNavigationPayload,
    required ShellRoutingState state,
    required DesktopActivationDelegate desktop,
  }) async {
    desktop.onShowDesktopCleared();

    if (app == 'settings' &&
        !hasNavigationPayload &&
        settingsSection != null &&
        settingsSection.isNotEmpty) {
      unawaited(
        desktop.requestShellOperation('settings', 'navigate', settingsSection),
      );
    }

    final existing = desktop.findOpenWindow(app);
    final focusedExisting = existing != null;
    if (existing != null) {
      desktop.focusWindow(existing);
    } else {
      unawaited(desktop.openApp(app));
    }

    if (!state.hasExplicitRoot && neuron != null && !changingBrain) {
      await onShowNeuron?.call(brain, neuron);
    }

    var navDelivered = false;
    if (neuron != null && hasNavigationPayload) {
      navDelivered = await dispatchNavigateTo(
        sendAct: (event) => sendAct(brain, event),
        targetNeuron: neuron,
        fromNeuron: fromNeuron,
        argsJson: argsJson,
        transition: transition,
        input: input,
      );
      if (navDelivered && _mounted) {
        desktop.onWindowNavigated(windowId: existing?.id, appId: app);
      }
    }

    if (state.hasExplicitRoot) {
      final receiver = state.activationApp;
      if (receiver != null) {
        await requestApp?.call(
          brain,
          receiver,
          'activate',
          jsonEncode(activation.toWireMap()),
        );
      }
      if (operation != null && !navDelivered && app != receiver) {
        await requestApp?.call(brain, app, operation, rawInput ?? '');
      }
    } else if (operation != null && !navDelivered) {
      await requestApp?.call(brain, app, operation, rawInput ?? '');
    }

    final standaloneActivation = activation is WorkspaceAppActivation
        ? WorkspaceAppActivation(
            application: activation.application,
            brainId: brain,
            neuron: activation.neuron,
            operation: activation.operation,
            input: activation.input,
            argsJson: activation.argsJson,
            rawTransition: activation.rawTransition,
            fromNeuron: activation.fromNeuron,
            settingsSection: activation.settingsSection,
            hasNavigationPayload: activation.hasNavigationPayload,
            extra: activation.extra,
            includeWireKind: activation.includeWireKind,
          )
        : activation;
    desktop.onNotifyStandaloneActivation?.call(standaloneActivation);

    return ShellActivationResult(
      handled: true,
      dispatchedNavigateTo: navDelivered,
      focusedExistingWindow: focusedExisting,
      openedNewWindow: !focusedExisting,
    );
  }

  Future<ShellActivationResult> _activateExplicitRoot({
    required String brain,
    required String? app,
    required String? neuron,
    required bool changingBrain,
    required ShellActivation activation,
    required String? operation,
    required String? input,
    required String? rawInput,
    required String? argsJson,
    required String transition,
    required String? fromNeuron,
    required bool hasNavigationPayload,
    required ShellRoutingState state,
  }) async {
    final receiver = state.activationApp;
    if (receiver == null && !state.isDesktopRoot) {
      if (app == null || neuron == null) {
        throw StateError('This root has no navigation receiver.');
      }
      final currentScreen = screenNeuronFromViewName(
        state.rootName,
        brain: brain,
      );
      if (!changingBrain && currentScreen != null && currentScreen != neuron) {
        final refusal = await checkDepartureGuard(
          sendAct: (event) => sendAct(brain, event),
          fromNeuron: currentScreen,
          toNeuron: neuron,
          route: argsJson ?? input,
        );
        if (refusal != null) {
          if (_mounted) {
            onDepartureRefused?.call(refusal);
            onToast?.call(refusal, tone: UiTone.error);
          }
          return ShellActivationResult(
            handled: true,
            refusedByDepartureGuard: true,
            refusalMessage: refusal,
          );
        }
      }
      var navDelivered = false;
      if (hasNavigationPayload) {
        navDelivered = await dispatchNavigateTo(
          sendAct: (event) => sendAct(brain, event),
          targetNeuron: neuron,
          fromNeuron: fromNeuron ?? currentScreen,
          argsJson: argsJson,
          transition: transition,
          input: input,
        );
      }
      if (operation != null && !navDelivered) {
        if (requestApp != null) {
          unawaited(
            requestApp!(
              brain,
              app,
              operation,
              rawInput ?? '',
            ).then<void>((_) {}, onError: (Object _) {}),
          );
        }
      }
      if (_mounted) {
        onSwitchExplicitSurface?.call(brain, app, neuron);
      }
      return ShellActivationResult(
        handled: true,
        dispatchedNavigateTo: navDelivered,
      );
    }

    if (receiver != null) {
      await requestApp?.call(
        brain,
        receiver,
        'activate',
        jsonEncode(activation.toWireMap()),
      );
    }
    var navDelivered = false;
    if (state.isDesktopRoot) {
      if (neuron != null && hasNavigationPayload) {
        navDelivered = await dispatchNavigateTo(
          sendAct: (event) => sendAct(brain, event),
          targetNeuron: neuron,
          fromNeuron: fromNeuron,
          argsJson: argsJson,
          transition: transition,
          input: input,
        );
      }
      if (operation != null &&
          !navDelivered &&
          app != null &&
          app != receiver) {
        await requestApp?.call(brain, app, operation, rawInput ?? '');
      }
    }
    return ShellActivationResult(
      handled: true,
      dispatchedNavigateTo: navDelivered,
    );
  }
}
