import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'apps/app_activations.dart';
import 'cross_window_router.dart';

/// A legacy `{kind: settings, brainId?, tab?}` (an older Ino card or Desktop
/// button) as the Settings app's workspace activation: open its window in that
/// brain and ask it for the tab (the app maps an old or no name).
Map<String, dynamic> settingsActivation(Map<String, dynamic> action) => {
  'kind': 'workspace',
  if (action['brainId'] case final String brain) 'brainId': brain,
  'application': 'settings',
  'operation': 'open',
  'input': action['tab'] is String ? action['tab'] as String : '',
};

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
  /// receive the normalized activation payload without re-entering the router.
  final ValueChanged<Map<String, dynamic>>? onNotifyStandaloneActivation;
}

/// Typed, normalized activation produced from a raw activation map or `os_uri`.
@immutable
class ParsedShellActivation {
  const ParsedShellActivation({
    required this.kind,
    required this.normalizedAction,
    this.brainId,
    this.appId,
    this.neuron,
    this.operation,
    this.input,
    this.argsJson,
    this.rawTransition,
    required this.transition,
    this.fromNeuron,
    this.settingsSection,
    this.hasNavigationPayload = false,
    this.isLegacySettings = false,
  });

  final String? kind;
  final Map<String, dynamic> normalizedAction;
  final String? brainId;
  final String? appId;
  final String? neuron;
  final String? operation;
  final String? input;
  final String? argsJson;
  final String? rawTransition;
  final String transition;
  final String? fromNeuron;
  final String? settingsSection;
  final bool hasNavigationPayload;
  final bool isLegacySettings;
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
/// Owns `os_uri` resolution, legacy `settings`/`configuration` normalization,
/// `ui.navigate-from` departure-guard checks, single-dispatch `ui.navigate-to`
/// delivery, brain switching, and desktop window open/focus coordination.
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
  final Future<void> Function(Map<String, dynamic> action)? onReconnect;
  final Future<void> Function(String brain, Map<String, dynamic> action)?
  onAppLifecycle;
  final void Function(String brain, Map<String, dynamic> action)?
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

  /// Resolves a raw activation map (including `os_uri`, legacy `settings`, and
  /// `configuration` views) into a single typed [ParsedShellActivation].
  /// Returns `null` when an `os_uri` is invalid or unrecognized.
  static ParsedShellActivation? parse(
    Map<String, dynamic> rawAction, {
    String? currentBrain,
    String? fallbackFromNeuron,
  }) {
    var action = Map<String, dynamic>.from(rawAction);
    String? settingsSection;

    if (action['kind'] == 'os_uri') {
      final uriStr = '${action['uri'] ?? ''}';
      final openRoute = openWindowRouteOf(
        uriStr,
        intent: action['intent'] as String?,
        fallbackFromNeuron: fallbackFromNeuron,
      );
      if (openRoute != null) {
        settingsSection = openRoute.section;
        if (openRoute.app == 'settings' && !openRoute.hasNavigationPayload) {
          action = {
            'kind': 'settings',
            if (action['brainId'] case final String b) 'brainId': b,
            if (openRoute.section case final String sec) 'tab': sec,
          };
        } else {
          action = {
            if (action['brainId'] case final String b) 'brainId': b,
            'kind': 'workspace',
            'application': openRoute.app,
            'operation': 'open',
            if (openRoute.input != null) 'input': openRoute.input,
            if (openRoute.argsJson != null) 'argsJson': openRoute.argsJson,
            if (openRoute.hasExplicitTransition)
              'transition': openRoute.transition,
            if (openRoute.fromNeuron != null)
              'fromNeuron': openRoute.fromNeuron,
            if (openRoute.hasNavigationPayload) 'hasNavigationPayload': true,
          };
        }
      } else if (uriStr.contains('settings')) {
        final uri = Uri.tryParse(uriStr);
        settingsSection = _nonEmptyField(uri?.queryParameters['section']);
        action = {
          'kind': 'settings',
          if (action['brainId'] case final String b) 'brainId': b,
          'tab': ?settingsSection,
        };
      } else {
        return null;
      }
    }

    final isLegacySettings = action['kind'] == 'settings';
    if (isLegacySettings) {
      action = settingsActivation(action);
    }

    if (action['view'] == 'configuration' && action['application'] is String) {
      action = configurationActivation(action);
    }

    final brain = _nonEmptyField(action['brainId']) ?? currentBrain;
    final app = _nonEmptyField(action['application']);
    final neuron =
        _nonEmptyField(action['neuron']) ??
        (app == null || brain == null ? null : '$brain/apps/$app');
    final navInput = _nonEmptyField(action['input']);
    final navArgsJson = _nonEmptyField(action['argsJson'] ?? action['args']);
    final rawTransition = _nonEmptyField(action['transition']);
    final navTransition = normalizeViewTransition(rawTransition);
    final fromNeuron = _nonEmptyField(action['fromNeuron']);
    final hasNavPayload =
        action['hasNavigationPayload'] == true ||
        navInput != null ||
        navArgsJson != null ||
        rawTransition != null;

    return ParsedShellActivation(
      kind: action['kind'] as String?,
      normalizedAction: action,
      brainId: brain,
      appId: app,
      neuron: neuron,
      operation: _nonEmptyField(action['operation']),
      input: navInput,
      argsJson: navArgsJson,
      rawTransition: rawTransition,
      transition: navTransition,
      fromNeuron: fromNeuron,
      settingsSection: settingsSection,
      hasNavigationPayload: hasNavPayload,
      isLegacySettings: isLegacySettings,
    );
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
    final transition = openRoute?.transition;
    final fromNeuron = openRoute?.fromNeuron;
    final hasNavPayload =
        effectiveInput != null || argsJson != null || hasExplicitTransition;

    if (appId == 'settings' && !hasNavPayload && operation == 'open') {
      return activate({
        'kind': 'settings',
        'brainId': ?effectiveBrain,
        if (settingsSection != null && settingsSection.isNotEmpty)
          'tab': settingsSection,
      });
    }

    return activate({
      'brainId': ?effectiveBrain,
      'kind': 'workspace',
      'application': appId,
      'operation': operation,
      'input': ?effectiveInput,
      'argsJson': ?argsJson,
      if (hasExplicitTransition) 'transition': ?transition,
      'fromNeuron': ?fromNeuron,
      if (hasNavPayload) 'hasNavigationPayload': true,
      ...?extra,
    });
  }

  /// Routes any shell activation (`os_uri`, `settings`, `workspace`,
  /// `connections`, `install`/`update`/`remove`, `reconnect`, `account`,
  /// `lobby-ended`) through one unified, single-dispatch state machine.
  Future<ShellActivationResult> activate(
    Map<String, dynamic> rawAction, {
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

    final parsed = parse(
      rawAction,
      currentBrain: state.currentBrain,
      fallbackFromNeuron: fallbackFrom,
    );
    if (parsed == null) {
      return const ShellActivationResult();
    }

    final action = parsed.normalizedAction;
    try {
      if (parsed.kind == 'account') {
        await onSwitchAccount?.call();
        return const ShellActivationResult(handled: true);
      }
      if (parsed.kind == 'lobby-ended') {
        await onEndLobby?.call();
        return const ShellActivationResult(handled: true);
      }
      if (parsed.kind == 'reconnect') {
        if (parsed.appId == 'desktop' && state.currentBrain != null) {
          await onReconnect?.call(action);
        }
        return const ShellActivationResult(handled: true);
      }

      final brain = parsed.brainId ?? state.currentBrain;
      if (brain == null || brain.isEmpty) {
        return const ShellActivationResult();
      }
      final app = parsed.appId;
      final neuron = parsed.neuron;
      final changingBrain = brain != state.currentBrain;

      if (const {'install', 'update', 'remove'}.contains(parsed.kind)) {
        if (changingBrain && listBrainIds != null) {
          final brains = await listBrainIds!();
          if (!brains.contains(brain)) {
            throw StateError('This account is not a member of that brain.');
          }
        }
        await onAppLifecycle?.call(brain, {...action, 'brainId': brain});
        return const ShellActivationResult(handled: true);
      }

      if (changingBrain) {
        if (listBrainIds != null) {
          final brains = await listBrainIds!();
          if (!brains.contains(brain)) {
            throw StateError('This account is not a member of that brain.');
          }
        }
        if (!_mounted) return const ShellActivationResult();
        await selectBrain?.call(
          brain,
          show:
              const {
                'connections',
                'install',
                'update',
                'remove',
              }.contains(parsed.kind)
              ? null
              : neuron,
        );
      } else if (app != null && app.trim().isNotEmpty) {
        onRegisterKnownApp?.call(app.trim().toLowerCase());
      }

      if (parsed.kind == 'connections') {
        if (_mounted) {
          onOpenConnections?.call(brain, action);
        }
        return const ShellActivationResult(handled: true);
      }

      final latestState = readState();
      final desktop = _desktopDelegate;

      // Desktop root path: coordinate local window manager & single navigate-to.
      if (latestState.isDesktopRoot && desktop != null && app != null) {
        return await _activateDesktopWindow(
          brain: brain,
          app: app,
          neuron: neuron,
          changingBrain: changingBrain,
          parsed: parsed,
          state: latestState,
          desktop: desktop,
        );
      }

      // Explicit root without desktop delegate.
      if (latestState.hasExplicitRoot) {
        return await _activateExplicitRoot(
          brain: brain,
          app: app,
          neuron: neuron,
          changingBrain: changingBrain,
          parsed: parsed,
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
          route: parsed.argsJson ?? parsed.input,
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
      if (neuron != null &&
          !parsed.isLegacySettings &&
          parsed.hasNavigationPayload) {
        navDelivered = await dispatchNavigateTo(
          sendAct: (event) => sendAct(brain, event),
          targetNeuron: neuron,
          fromNeuron: parsed.fromNeuron ?? previousScreen,
          argsJson: parsed.argsJson,
          transition: parsed.transition,
          input: parsed.input,
        );
      }

      if (parsed.operation case final String operation
          when !navDelivered && app != null) {
        await requestApp?.call(brain, app, operation, parsed.input ?? '');
      }

      return ShellActivationResult(
        handled: true,
        dispatchedNavigateTo: navDelivered,
      );
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

  Future<ShellActivationResult> _activateDesktopWindow({
    required String brain,
    required String app,
    required String? neuron,
    required bool changingBrain,
    required ParsedShellActivation parsed,
    required ShellRoutingState state,
    required DesktopActivationDelegate desktop,
  }) async {
    desktop.onShowDesktopCleared();

    final shouldNavigateTo =
        !parsed.isLegacySettings && parsed.hasNavigationPayload;
    final settingsSection = parsed.settingsSection;
    if (app == 'settings' &&
        !shouldNavigateTo &&
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
    if (neuron != null && shouldNavigateTo) {
      navDelivered = await dispatchNavigateTo(
        sendAct: (event) => sendAct(brain, event),
        targetNeuron: neuron,
        fromNeuron: parsed.fromNeuron,
        argsJson: parsed.argsJson,
        transition: parsed.transition,
        input: parsed.input,
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
          jsonEncode(parsed.normalizedAction),
        );
      }
      if (parsed.operation case final String operation
          when !navDelivered && app != receiver) {
        await requestApp?.call(brain, app, operation, parsed.input ?? '');
      }
    } else if (parsed.operation case final String operation
        when !navDelivered) {
      await requestApp?.call(brain, app, operation, parsed.input ?? '');
    }

    desktop.onNotifyStandaloneActivation?.call({
      'brainId': brain,
      ...parsed.normalizedAction,
    });

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
    required ParsedShellActivation parsed,
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
          route: parsed.argsJson ?? parsed.input,
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
      if (!parsed.isLegacySettings && parsed.hasNavigationPayload) {
        navDelivered = await dispatchNavigateTo(
          sendAct: (event) => sendAct(brain, event),
          targetNeuron: neuron,
          fromNeuron: parsed.fromNeuron ?? currentScreen,
          argsJson: parsed.argsJson,
          transition: parsed.transition,
          input: parsed.input,
        );
      }
      if (parsed.operation case final String operation when !navDelivered) {
        if (requestApp != null) {
          unawaited(
            requestApp!(
              brain,
              app,
              operation,
              parsed.input ?? '',
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
        jsonEncode(parsed.normalizedAction),
      );
    }
    var navDelivered = false;
    if (state.isDesktopRoot) {
      if (neuron != null &&
          !parsed.isLegacySettings &&
          parsed.hasNavigationPayload) {
        navDelivered = await dispatchNavigateTo(
          sendAct: (event) => sendAct(brain, event),
          targetNeuron: neuron,
          fromNeuron: parsed.fromNeuron,
          argsJson: parsed.argsJson,
          transition: parsed.transition,
          input: parsed.input,
        );
      }
      if (parsed.operation case final String operation
          when !navDelivered && app != null && app != receiver) {
        await requestApp?.call(brain, app, operation, parsed.input ?? '');
      }
    }
    return ShellActivationResult(
      handled: true,
      dispatchedNavigateTo: navDelivered,
    );
  }
}
