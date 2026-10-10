import 'package:digitalbrain_flutter_shell/workspace/dock/assistant_island.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'application_fixture.dart';

void main() {
  testWidgets(
    'a focused app without a toolbar leaves the assistant island in the dock',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final fixture = await ApplicationFixture.start(tester);

      await fixture.openApp('assistant');
      expect(
        fixture.store.focusedWindow,
        '${fixture.brainId}/apps/assistant/window',
      );
      expect(find.byKey(const ValueKey('assistant-ino')), findsOneWidget);
      expect(find.byType(DockTextField), findsOneWidget);

      await tester.tap(find.byTooltip('Home'));
      await tester.pumpAndSettle();
      expect(find.byKey(const ValueKey('running-assistant')), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('stop-assistant')));
      await tester.pumpAndSettle();
      expect(
        fixture.server.requestsTo(
          '/brains/${fixture.brainId}/windows/${Uri.encodeComponent('${fixture.brainId}/apps/assistant/window')}/close',
        ),
        isNotEmpty,
      );
      await fixture.dispose();
    },
  );
}
