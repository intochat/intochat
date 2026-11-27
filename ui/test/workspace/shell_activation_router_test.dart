import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/workspace/shell_activation_router.dart';

DesktopWindowProjection _window(String id, String appId) =>
    DesktopWindowProjection(
      id: id,
      appId: appId,
      title: appId,
      viewportUri: 'brain-1/apps/desktop/window/$id',
      tabs: [
        DesktopTabProjection(
          id: '$id/0',
          appId: appId,
          title: appId,
          selected: true,
        ),
      ],
      activeTabId: '$id/0',
      isPinned: false,
      isFavourite: false,
    );

void main() {
  group('ShellActivationRouter', () {
    test('dispatches ui.navigate-to exactly once for os_uri on new and existing desktop windows', () async {
      final actEvents = <Map<String, Object?>>[];
      final openedApps = <String>[];
      final focusedWindows = <String>[];
      final navigatedReloads = <(String?, String?)>[];
      final openWindows = <String, DesktopWindowProjection>{};

      final router = ShellActivationRouter(
        readState: () => const ShellRoutingState(
          currentBrain: 'brain-1',
          isDesktopRoot: true,
        ),
        sendAct: (_, event) async {
          actEvents.add(event);
          return const {'delivered': true};
        },
        desktopDelegate: DesktopActivationDelegate(
          findOpenWindow: (appId) => openWindows[appId],
          focusWindow: (win) => focusedWindows.add(win.id),
          openApp: (appId) async {
            openedApps.add(appId);
            openWindows[appId] = _window('w-${openedApps.length}', appId);
          },
          onShowDesktopCleared: () {},
          onWindowNavigated: ({windowId, appId}) =>
              navigatedReloads.add((windowId, appId)),
          requestShellOperation: (_, _, _) async {},
        ),
      );

      final argsJson = jsonEncode({'traceId': 'trace-123'});
      final uri =
          'os://window/open?app=aspire&args=${Uri.encodeQueryComponent(argsJson)}&transition=shared-axis-z';

      // 1. Opening a new window dispatches ui.navigate-to once.
      final first = await router.activate({
        'kind': 'os_uri',
        'uri': uri,
      }, sourceAppId: 'engineer');
      expect(first.handled, isTrue);
      expect(first.openedNewWindow, isTrue);
      expect(first.dispatchedNavigateTo, isTrue);
      expect(openedApps, ['aspire']);
      expect(
        actEvents.where((e) => e['signal'] == 'ui.navigate-to'),
        hasLength(1),
      );
      final firstArgs = actEvents.single['args'] as Map<String, Object?>;
      expect(firstArgs['argsJson'], argsJson);
      expect(firstArgs['transition'], 'shared-axis-z');
      expect(firstArgs['fromNeuron'], 'brain-1/apps/engineer');
      expect(navigatedReloads, [(null, 'aspire')]);

      // 2. Focusing an already-open window dispatches ui.navigate-to once more (total 2, never double).
      final second = await router.activate({
        'kind': 'os_uri',
        'uri': 'os://window/open?app=aspire&input=trace-456',
      }, sourceAppId: 'engineer');
      expect(second.handled, isTrue);
      expect(second.focusedExistingWindow, isTrue);
      expect(second.dispatchedNavigateTo, isTrue);
      expect(focusedWindows, ['w-1']);
      expect(
        actEvents.where((e) => e['signal'] == 'ui.navigate-to'),
        hasLength(2),
      );
      expect(navigatedReloads.last, ('w-1', 'aspire'));
    });

    test('enforces departure guard Rule rejections and fails open on unsupported handlers', () async {
      var blockWithRule = true;
      final refused = <String>[];
      final toasts = <String>[];
      final switchedTo = <String>[];
      final actEvents = <Map<String, Object?>>[];

      final router = ShellActivationRouter(
        readState: () => const ShellRoutingState(
          currentBrain: 'brain-1',
          isDesktopRoot: false,
          hasExplicitRoot: true,
          rootName: 'brain-1/apps/engineer/view',
          focusedNeuron: 'brain-1/apps/engineer',
        ),
        sendAct: (_, event) async {
          actEvents.add(event);
          if (event['signal'] == 'ui.navigate-from') {
            if (blockWithRule) {
              return {
                'error': 'Refused',
                'reason': 'Rule:unsaved_draft: Save draft first.',
              };
            }
            return {'error': 'No handler', 'reason': 'HandlerFailed'};
          }
          return {'delivered': true};
        },
        onDepartureRefused: refused.add,
        onToast: (msg, {UiTone tone = UiTone.neutral}) => toasts.add(msg),
        onSwitchExplicitSurface: (_, app, _) => switchedTo.add(app),
      );

      final blocked = await router.activate({
        'kind': 'os_uri',
        'uri': 'os://window/open?app=aspire&input=trace-1',
      });
      expect(blocked.refusedByDepartureGuard, isTrue);
      expect(blocked.refusalMessage, 'Save draft first.');
      expect(refused, ['Save draft first.']);
      expect(toasts, ['Save draft first.']);
      expect(switchedTo, isEmpty);
      expect(actEvents.where((e) => e['signal'] == 'ui.navigate-to'), isEmpty);

      // Now fail open when handler is unsupported.
      blockWithRule = false;
      final allowed = await router.activate({
        'kind': 'os_uri',
        'uri': 'os://window/open?app=aspire&input=trace-1',
      });
      expect(allowed.refusedByDepartureGuard, isFalse);
      expect(allowed.dispatchedNavigateTo, isTrue);
      expect(switchedTo, ['aspire']);
      expect(
        actEvents.where((e) => e['signal'] == 'ui.navigate-to'),
        hasLength(1),
      );
    });

    test('routes os_uri settings and legacy settings activations without ui.navigate-to', () async {
      final actEvents = <Map<String, Object?>>[];
      final shellOps = <(String, String, String)>[];
      final appRequests = <(String, String, String, String)>[];
      final openedApps = <String>[];

      final router = ShellActivationRouter(
        readState: () => const ShellRoutingState(
          currentBrain: 'brain-1',
          isDesktopRoot: true,
        ),
        sendAct: (_, event) async {
          actEvents.add(event);
          return const {'delivered': true};
        },
        requestApp: (brain, app, op, input) async =>
            appRequests.add((brain, app, op, input)),
        desktopDelegate: DesktopActivationDelegate(
          findOpenWindow: (_) => null,
          focusWindow: (_) {},
          openApp: (app) async => openedApps.add(app),
          onShowDesktopCleared: () {},
          onWindowNavigated: ({windowId, appId}) {},
          requestShellOperation: (app, op, input) async =>
              shellOps.add((app, op, input)),
        ),
      );

      await router.activate({
        'kind': 'os_uri',
        'uri': 'os://window/open?app=settings&section=billing',
      });
      expect(openedApps, ['settings']);
      expect(shellOps, [('settings', 'navigate', 'billing')]);
      expect(actEvents, isEmpty);

      await router.activate({'kind': 'settings', 'tab': 'voice'});
      expect(appRequests.last, ('brain-1', 'settings', 'open', 'voice'));
      expect(actEvents, isEmpty);
    });

    test('validates brain membership on brain-switch activation and rejects unauthorized brains', () async {
      final selectedBrains = <String>[];
      final toasts = <String>[];

      final router = ShellActivationRouter(
        readState: () => const ShellRoutingState(
          currentBrain: 'brain-1',
          isDesktopRoot: false,
        ),
        sendAct: (_, _) async => const {'delivered': true},
        listBrainIds: () async => ['brain-1', 'brain-2'],
        selectBrain: (brain, {show}) async => selectedBrains.add(brain),
        onToast: (msg, {UiTone tone = UiTone.neutral}) => toasts.add(msg),
      );

      await router.activate({
        'kind': 'workspace',
        'brainId': 'brain-unauthorized',
        'application': 'ino',
        'operation': 'open',
      });
      expect(selectedBrains, isEmpty);
      expect(toasts, ['This account is not a member of that brain.']);

      await router.activate({
        'kind': 'workspace',
        'brainId': 'brain-2',
        'application': 'ino',
        'operation': 'open',
      });
      expect(selectedBrains, ['brain-2']);
    });
  });
}
