import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Widget wrap(Widget child) {
    return MaterialApp(
      theme: ThemeData(
        useMaterial3: true,
        extensions: [IcColors.forPreset(IcStylePreset.warmPaper, dark: false)],
      ),
      home: Scaffold(body: Center(child: child)),
    );
  }

  testWidgets(
    'ViewHost watches IScreen ViewChanged on apps/engineer/view and reloads immediately on a server push with zero timers',
    (tester) async {
      var activeTab = 'plan';
      var viewLoads = 0;
      var activeViewListeners = 0;
      var activeScreenListeners = 0;
      final watchedTargets = <(String, String)>[];

      final viewEvents = StreamController<void>.broadcast(
        onListen: () => activeViewListeners++,
        onCancel: () => activeViewListeners--,
      );
      final screenEvents = StreamController<void>.broadcast(
        onListen: () => activeScreenListeners++,
        onCancel: () => activeScreenListeners--,
      );

      await tester.pumpWidget(
        wrap(
          ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: 'owner/apps/engineer/view',
            watch: (kind, name) {
              watchedTargets.add((kind, name));
              if (kind == 'screen') return screenEvents.stream;
              return viewEvents.stream;
            },
            load: (_, _) async {
              viewLoads++;
              return {
                'alias': 'app.engineer',
                'generation': viewLoads,
                'view': {
                  'title': 'Engineer',
                  'acts': 'act',
                  'root': {
                    'kind': 'column',
                    'children': [
                      {'kind': 'text', 'value': 'Pane:$activeTab'},
                    ],
                  },
                },
              };
            },
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Pane:plan'), findsOneWidget);
      expect(viewLoads, 1);
      expect(
        watchedTargets,
        contains(('screen', 'owner/app.engineer/default')),
      );
      expect(activeViewListeners, 0);
      expect(activeScreenListeners, 1);

      // Without a ViewChanged push nothing reloads: no timers.
      activeTab = 'pr';
      await tester.pump(const Duration(seconds: 1));
      expect(viewLoads, 1, reason: 'ViewHost must never poll on timers');
      expect(find.text('Pane:plan'), findsOneWidget);

      // Pushing ViewChanged from app.engineer reloads ViewHost immediately with zero timers.
      screenEvents.add(null);
      await tester.pump();
      await tester.pump();
      expect(viewLoads, 2);
      expect(find.text('Pane:pr'), findsOneWidget);

      // Server-initiated IScreen state change reloads ViewHost immediately too.
      activeTab = 'verify';
      screenEvents.add(null);
      await tester.pump();
      await tester.pump();
      expect(viewLoads, 3);
      expect(find.text('Pane:verify'), findsOneWidget);

      // Immediate dispose cancels the active stream subscription with zero pending timers.
      await tester.pumpWidget(const SizedBox());
      expect(activeViewListeners, 0);
      expect(activeScreenListeners, 0);

      await viewEvents.close();
      await screenEvents.close();
    },
  );

  testWidgets(
    'ViewHost coalesces ViewChanged pushes arriving while initial or prior IScreen reload is in flight and cleans up on mid-reload dispose',
    (tester) async {
      var activeScreenListeners = 0;
      final watchedTargets = <(String, String)>[];
      final screenEvents = StreamController<void>.broadcast(
        onListen: () => activeScreenListeners++,
        onCancel: () => activeScreenListeners--,
      );

      final pendingLoads = <Completer<Map<String, dynamic>>>[];
      Map<String, dynamic> payload(int gen, String tab) => {
        'alias': 'app.creator',
        'generation': gen,
        'view': {
          'title': 'Creator',
          'acts': 'act',
          'root': {'kind': 'text', 'value': 'Inspector:$tab#$gen'},
        },
      };

      await tester.pumpWidget(
        wrap(
          ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: 'owner/app.creator/default',
            watch: (kind, name) {
              watchedTargets.add((kind, name));
              return screenEvents.stream;
            },
            load: (_, _) {
              final completer = Completer<Map<String, dynamic>>();
              pendingLoads.add(completer);
              return completer.future;
            },
          ),
        ),
      );

      // Initial _reload() #1 is in flight and already subscribed to ('screen', 'owner/app.creator/default').
      expect(pendingLoads, hasLength(1));
      expect(watchedTargets, [('screen', 'owner/app.creator/default')]);
      expect(activeScreenListeners, 1);

      // A ViewChanged push arrives while initial _reload() #1 is still in flight.
      screenEvents.add(null);
      await tester.pump();
      expect(pendingLoads, hasLength(1));

      // Once initial _reload() #1 settles, ViewHost immediately issues follow-up reload #2.
      pendingLoads[0].complete(payload(1, 'State'));
      await tester.pump();
      expect(pendingLoads, hasLength(2));

      pendingLoads[1].complete(payload(2, 'Source'));
      await tester.pumpAndSettle();
      expect(find.text('Inspector:Source#2'), findsOneWidget);

      // Now trigger reload #3 and emit two rapid ViewChanged pushes while reload #3 is in flight.
      screenEvents.add(null);
      await tester.pump();
      expect(pendingLoads, hasLength(3));

      screenEvents.add(null);
      screenEvents.add(null);
      await tester.pump();
      expect(
        pendingLoads,
        hasLength(3),
        reason: 'Overlapping pushes coalesce while reload #3 is in flight',
      );

      pendingLoads[2].complete(payload(3, 'Signals'));
      await tester.pump();
      expect(
        pendingLoads,
        hasLength(4),
        reason: 'Coalesced mid-flight push triggers one clean follow-up reload',
      );

      pendingLoads[3].complete(payload(4, 'Scenarios'));
      await tester.pumpAndSettle();
      expect(find.text('Inspector:Scenarios#4'), findsOneWidget);

      // Dispose while reload #5 is still in flight: zero leaked subscriptions or setState-after-dispose.
      screenEvents.add(null);
      await tester.pump();
      expect(pendingLoads, hasLength(5));

      await tester.pumpWidget(const SizedBox());
      expect(activeScreenListeners, 0);

      pendingLoads[4].complete(payload(5, 'Disposed'));
      await tester.pump();
      expect(pendingLoads, hasLength(5));

      await screenEvents.close();
    },
  );
}
