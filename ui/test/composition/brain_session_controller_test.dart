import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  group('BrainSessionController lifecycle & brain switch isolation', () {
    test('switchBrain cancels all 4 stream subscriptions (desktop, settings, presence, notifications) and drops stale brain events', () async {
      final activeSubs = <String, StreamController<void>>{};
      final activeSignals = <String, StreamController<Map<String, dynamic>>>{};
      final openedKeys = <String>[];
      final cancelledKeys = <String>[];
      var seq = 0;

      Stream<void> watchNode(String kind, String name) {
        final id = '#${++seq}:$kind:$name';
        openedKeys.add(id);
        late StreamController<void> controller;
        controller = StreamController<void>(
          onCancel: () => cancelledKeys.add(id),
        );
        activeSubs[id] = controller;
        return controller.stream;
      }

      Stream<Map<String, dynamic>> watchSignal(
        String contract,
        String key,
        String signal,
      ) {
        final id = '#${++seq}:$contract:$key:$signal';
        openedKeys.add(id);
        late StreamController<Map<String, dynamic>> controller;
        controller = StreamController<Map<String, dynamic>>(
          onCancel: () => cancelledKeys.add(id),
        );
        activeSignals[id] = controller;
        return controller.stream;
      }

      final brain1LoadCompleter = Completer<DesktopShellProjection>();
      final loadedBrains = <String>[];
      final remotePresets = <IcStylePreset>[];

      final controller = BrainSessionController(
        brainId: 'brain-1',
        reconnectDelay: const Duration(hours: 1),
        loadProjection: (brain) {
          loadedBrains.add(brain);
          if (brain == 'brain-1' && !brain1LoadCompleter.isCompleted) {
            return brain1LoadCompleter.future;
          }
          return Future.value(
            DesktopShellProjection(
              windows: [
                DesktopWindowProjection(
                  id: 'w-1',
                  appId: 'ino',
                  title: 'Ino ($brain)',
                  viewportUri: '$brain/apps/desktop/window/w-1',
                  tabs: const [],
                  activeTabId: 'w-1/0',
                  isPinned: true,
                  isFavourite: false,
                ),
              ],
              focusedViewId: 'w-1/0',
              pins: const ['ino'],
              tiles: const [],
              openWidgets: [brain],
            ),
          );
        },
        watchNode: watchNode,
        watchSignal: watchSignal,
        readContract: (contract, key, method) async {
          if (method == 'Read' && key == 'settings') {
            return {
              'variables': [
                {'key': 'ino.smart', 'value': 'on'},
                {'key': 'ui.style_preset', 'value': 'warm-paper'},
              ],
            };
          }
          if (method == 'Read') {
            return {'state': 'Waiting', 'revision': 1};
          }
          if (method == 'Pending') {
            return {
              'value': [{}],
            };
          }
          return null;
        },
        onRemoteStylePreset: remotePresets.add,
        desktopEnabled: true,
      );
      addTearDown(controller.dispose);

      // Allow async settings read -> presence subscription to attach for brain-1.
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);

      final brain1Subs = List<String>.from(openedKeys);
      expect(brain1Subs, hasLength(4));
      expect(
        brain1Subs.any(
          (k) => k.endsWith(':screen:brain-1/app.desktop/default'),
        ),
        isTrue,
      );
      expect(
        brain1Subs.any((k) => k.endsWith(':os.settings:settings')),
        isTrue,
      );
      expect(
        brain1Subs.any((k) => k.endsWith(':ino.presence:ino/presence')),
        isTrue,
      );
      expect(
        brain1Subs.any(
          (k) => k.endsWith(
            ':IntoChat.OS.Modules.Notifications.INotificationsInbox:inbox:NotificationSent',
          ),
        ),
        isTrue,
      );
      expect(controller.smart, isTrue);
      expect(controller.unreadNotifications, 1);
      expect(controller.presenceState, PresenceState.waiting);

      // Switch brain while brain-1's initial projection request is still in-flight!
      controller.switchBrain('brain-2', desktopEnabled: true);
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);

      // ALL 4 brain-1 subscriptions MUST have been cancelled!
      expect(cancelledKeys, containsAll(brain1Subs));

      // Brain-2 subscriptions are now active and brain-2 projection is loaded.
      expect(controller.brainId, 'brain-2');
      expect(controller.projection?.openWidgets, ['brain-2']);
      final brain2Subs = openedKeys.skip(brain1Subs.length).toList();
      expect(brain2Subs, hasLength(4));
      expect(
        brain2Subs.any(
          (k) => k.endsWith(':screen:brain-2/app.desktop/default'),
        ),
        isTrue,
      );
      expect(
        brain2Subs.any((k) => k.endsWith(':os.settings:settings')),
        isTrue,
      );
      expect(
        brain2Subs.any((k) => k.endsWith(':ino.presence:ino/presence')),
        isTrue,
      );
      expect(
        brain2Subs.any(
          (k) => k.endsWith(
            ':IntoChat.OS.Modules.Notifications.INotificationsInbox:inbox:NotificationSent',
          ),
        ),
        isTrue,
      );

      // Complete the stale brain-1 in-flight future -> MUST NOT overwrite brain-2 projection!
      brain1LoadCompleter.complete(
        const DesktopShellProjection(
          windows: [],
          pins: [],
          tiles: [],
          openWidgets: ['stale-brain-1'],
        ),
      );
      await Future<void>.delayed(Duration.zero);
      expect(controller.projection?.openWidgets, ['brain-2']);

      // Dispose cancels all brain-2 subscriptions cleanly.
      controller.dispose();
      await Future<void>.delayed(Duration.zero);
      expect(cancelledKeys, containsAll(brain2Subs));

      for (final c in activeSubs.values) {
        await c.close();
      }
      for (final c in activeSignals.values) {
        await c.close();
      }
    });

    test('coalesces concurrent reloadDesktop calls without dropping trailing updates', () async {
      var loadCount = 0;
      final firstLoad = Completer<DesktopShellProjection>();

      final controller = BrainSessionController(
        brainId: 'brain-1',
        loadProjection: (_) {
          loadCount++;
          if (loadCount == 1) return firstLoad.future;
          return Future.value(
            DesktopShellProjection(
              windows: const [],
              pins: const [],
              tiles: const [],
              openWidgets: ['rev-$loadCount'],
            ),
          );
        },
      );
      addTearDown(controller.dispose);

      // Fire 3 reloads while the first is still in flight.
      final f1 = controller.reloadDesktop();
      final f2 = controller.reloadDesktop();
      final f3 = controller.reloadDesktop();
      expect(loadCount, 1);

      firstLoad.complete(
        const DesktopShellProjection(
          windows: [],
          pins: [],
          tiles: [],
          openWidgets: ['rev-1'],
        ),
      );
      await Future.wait([f1, f2, f3]);

      // Exactly 2 loads ran: the initial load + 1 coalesced trailing reload.
      expect(loadCount, 2);
      expect(controller.projection?.openWidgets, ['rev-2']);
    });

    testWidgets(
      'BrainSessionScope.readOf does not subscribe while of(context) rebuilds dependents',
      (tester) async {
        var ofBuilds = 0;
        var readOfBuilds = 0;
        var generation = 1;

        final controller = BrainSessionController(
          brainId: 'brain-1',
          loadProjection: (_) async => DesktopShellProjection(
            windows: const [],
            pins: const [],
            tiles: const [],
            openWidgets: ['gen-$generation'],
          ),
        );
        addTearDown(controller.dispose);

        await tester.pumpWidget(
          BrainSessionScope(
            controller: controller,
            child: Directionality(
              textDirection: TextDirection.ltr,
              child: Column(
                children: [
                  Builder(
                    builder: (context) {
                      ofBuilds++;
                      final s = BrainSessionScope.of(context);
                      return Text(
                        s.projection?.openWidgets.join(',') ?? 'empty',
                      );
                    },
                  ),
                  Builder(
                    builder: (context) {
                      readOfBuilds++;
                      final s = BrainSessionScope.readOf(context)!;
                      return Text('reader:${s.brainId}');
                    },
                  ),
                ],
              ),
            ),
          ),
        );

        expect(ofBuilds, 1);
        expect(readOfBuilds, 1);

        await controller.reloadDesktop();
        await tester.pump();
        expect(ofBuilds, 2);
        expect(readOfBuilds, 1);
        expect(find.text('gen-1'), findsOneWidget);

        generation = 2;
        await controller.reloadDesktop();
        await tester.pump();
        expect(ofBuilds, 3);
        expect(readOfBuilds, 1);
        expect(find.text('gen-2'), findsOneWidget);
      },
    );
  });
}
