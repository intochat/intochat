import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/dock/assistant_island.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

Widget host(
  StreamController<AppActivity> feed, {
  bool floating = false,
  bool reduced = false,
}) => MaterialApp(
  home: MediaQuery(
    data: MediaQueryData(disableAnimations: reduced),
    child: Scaffold(
      body: Center(
        child: AssistantIsland(watch: () => feed.stream, floating: floating),
      ),
    ),
  ),
);

InoPresenceState shown(WidgetTester tester) =>
    tester.widget<InoPresence>(find.byType(InoPresence)).state;

void main() {
  testWidgets(
    'Ino follows the latest assistant activity and shows its detail',
    (tester) async {
      final feed = StreamController<AppActivity>();
      addTearDown(feed.close);
      await tester.pumpWidget(host(feed));
      expect(shown(tester), InoPresenceState.idle);
      expect(find.byKey(const ValueKey('ino-caption')), findsNothing);

      feed.add(
        const AppActivity(
          app: 'assistant',
          state: 'working',
          detail: 'Reading your mail',
        ),
      );
      await tester.pump();
      expect(shown(tester), InoPresenceState.working);
      expect(find.text('Reading your mail'), findsOneWidget);

      feed.add(
        const AppActivity(app: 'files', state: 'attention', detail: 'not mine'),
      );
      await tester.pump();
      expect(shown(tester), InoPresenceState.working);
      expect(find.text('not mine'), findsNothing);

      feed.add(
        const AppActivity(app: 'assistant', state: 'completed', detail: ''),
      );
      await tester.pump();
      expect(shown(tester), InoPresenceState.completed);
      expect(find.byKey(const ValueKey('ino-caption')), findsNothing);
    },
  );

  testWidgets('an unknown state falls back to idle', (tester) async {
    final feed = StreamController<AppActivity>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed));
    feed.add(const AppActivity(app: 'assistant', state: 'dancing', detail: ''));
    await tester.pump();
    expect(shown(tester), InoPresenceState.idle);
  });

  testWidgets('a failing live feed leaves Ino disconnected', (tester) async {
    final feed = StreamController<AppActivity>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed));
    feed.add(
      const AppActivity(app: 'assistant', state: 'working', detail: 'x'),
    );
    await tester.pump();
    feed.addError(StateError('feed lost'));
    await tester.pump();
    expect(shown(tester), InoPresenceState.disconnected);
    expect(find.byKey(const ValueKey('ino-caption')), findsNothing);
  });

  testWidgets('with no app focused Ino floats', (tester) async {
    final feed = StreamController<AppActivity>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed, floating: true));
    final start = tester.getTopLeft(find.byType(InoPresence)).dy;
    await tester.pump(const Duration(milliseconds: 1300));
    expect(tester.getTopLeft(find.byType(InoPresence)).dy, lessThan(start));
  });

  testWidgets('reduced motion keeps a floating Ino still', (tester) async {
    final feed = StreamController<AppActivity>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed, floating: true, reduced: true));
    final start = tester.getTopLeft(find.byType(InoPresence));
    await tester.pump(const Duration(milliseconds: 1300));
    expect(tester.getTopLeft(find.byType(InoPresence)), start);
  });

  testWidgets('a docked Ino does not float', (tester) async {
    final feed = StreamController<AppActivity>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed));
    final start = tester.getTopLeft(find.byType(InoPresence));
    await tester.pump(const Duration(milliseconds: 1300));
    expect(tester.getTopLeft(find.byType(InoPresence)), start);
  });
}
