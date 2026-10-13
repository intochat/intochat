import 'package:digitalbrain_flutter_shell/workspace/dock/assistant_island.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/home_screen.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'application_fixture.dart';

void main() {
  testWidgets(
    'with nothing open, Home is in front and the dock is the assistant island',
    (tester) async {
      final fixture = await ApplicationFixture.start(tester);

      expect(find.byType(HomeScreen), findsOneWidget);
      expect(find.byKey(const ValueKey('assistant-ino')), findsOneWidget);
      expect(find.byType(DockTextField), findsOneWidget);
      expect(find.byType(ApplicationSurface), findsNothing);
      expect(
        fixture.server.requestsTo(
          '/brains/${fixture.brainId}/apps/assistant/request',
        ),
        isEmpty,
      );

      // Submitting the island's field posts "send" to the assistant; nothing waits for an answer.
      await tester.enterText(find.byType(DockTextField), 'hello');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      final sent = fixture.server
          .requestsTo('/brains/${fixture.brainId}/apps/assistant/request')
          .single;
      expect((sent.body['operation'], sent.body['input']), ('send', 'hello'));
      await fixture.dispose();
    },
  );

  testWidgets(
    'opening an app reveals its declared entry without executing an operation',
    (tester) async {
      final fixture = await ApplicationFixture.start(tester);

      await fixture.openApp('assistant');

      final request = fixture.server.requests.singleWhere(
        (request) => request.path.endsWith('/show'),
      );
      expect(request.body['app'], '${fixture.brainId}/apps/assistant');
      expect(
        fixture.renderedSurface,
        '${fixture.brainId}/apps/assistant/surface',
      );
      expect(find.byType(NeuronView), findsWidgets);
      expect(find.byType(DockTextField), findsOneWidget);
      await fixture.dispose();
    },
  );

  testWidgets('each Assistant launch focuses the same declared entry', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final fixture = await ApplicationFixture.start(tester);

    await fixture.openApp('assistant');
    await fixture.openApp('assistant');

    final posted = fixture.server.requests
        .where((request) => request.path.endsWith('/show'))
        .length;
    expect(posted, 2);
    expect(fixture.store.currentProject.artifacts.map((a) => a.id), [
      '${fixture.brainId}/apps/assistant',
    ]);
    expect(fixture.store.focusedWindow, '${fixture.brainId}/apps/assistant');
    expect(find.byType(DockTextField), findsOneWidget);
    expect(
      fixture.renderedSurface,
      '${fixture.brainId}/apps/assistant/surface',
    );
    await fixture.dispose();
  });

  testWidgets(
    'pressing Home preserves the catalog without starting Assistant',
    (tester) async {
      final fixture = await ApplicationFixture.start(tester);
      await tester.tap(find.byTooltip('Home'));
      await tester.pumpAndSettle();
      expect(find.byType(HomeScreen), findsOneWidget);
      expect(find.byType(ApplicationSurface), findsNothing);
      expect(
        fixture.server.requestsTo(
          '/brains/${fixture.brainId}/apps/assistant/request',
        ),
        isEmpty,
      );
      expect(fixture.store.focusedWindow, isNull);
      await fixture.dispose();
    },
  );

  test(
    'preferences no longer carry a dock, controls position or side panel side',
    () {
      final json = WorkspacePreferences().toJson();
      expect(json.containsKey('dock'), isFalse);
      expect(json.containsKey('dockTop'), isFalse);
      expect(json.containsKey('assistantRight'), isFalse);
    },
  );
}
