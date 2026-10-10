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
}) => hostWatch(() => feed.stream, floating: floating, reduced: reduced);

Widget hostWatch(
  Stream<AppActivity> Function() watch, {
  String? brainId,
  bool floating = false,
  bool reduced = false,
}) => MaterialApp(
  home: MediaQuery(
    data: MediaQueryData(disableAnimations: reduced),
    child: Scaffold(
      body: Center(
        child: AssistantIsland(
          watch: watch,
          brainId: brainId,
          floating: floating,
          retryAfter: const Duration(seconds: 1),
        ),
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

  testWidgets('an unknown state is ignored and keeps the current one', (
    tester,
  ) async {
    final feed = StreamController<AppActivity>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed));
    feed.add(
      const AppActivity(app: 'assistant', state: 'working', detail: 'x'),
    );
    await tester.pump();
    feed.add(const AppActivity(app: 'assistant', state: 'dancing', detail: ''));
    await tester.pump();
    expect(shown(tester), InoPresenceState.working);
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

  testWidgets('a feed that ends shows disconnected and is reopened', (
    tester,
  ) async {
    final feeds = <StreamController<AppActivity>>[];
    await tester.pumpWidget(
      hostWatch(() {
        final feed = StreamController<AppActivity>();
        feeds.add(feed);
        return feed.stream;
      }),
    );
    feeds.first.add(
      const AppActivity(app: 'assistant', state: 'working', detail: 'x'),
    );
    await tester.pump();
    await feeds.first.close();
    await tester.pump();
    expect(shown(tester), InoPresenceState.disconnected);
    expect(feeds, hasLength(1));

    await tester.pump(const Duration(seconds: 1));
    expect(feeds, hasLength(2));
    feeds.last.add(
      const AppActivity(app: 'assistant', state: 'completed', detail: 'back'),
    );
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 10));
    expect(shown(tester), InoPresenceState.completed);
    expect(find.text('back'), findsOneWidget);
    await feeds.last.close();
    await tester.pump();
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('repeated failures wait longer each time', (tester) async {
    var opened = 0;
    await tester.pumpWidget(
      hostWatch(() {
        opened++;
        return Stream<AppActivity>.error(StateError('down'));
      }),
    );
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    expect(opened, 2);
    await tester.pump(const Duration(seconds: 1));
    expect(opened, 2);
    await tester.pump(const Duration(seconds: 1));
    expect(opened, 3);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a different brain reopens the feed', (tester) async {
    final opened = <String>[];
    Widget forBrain(String brain) => hostWatch(() {
      opened.add(brain);
      return const Stream<AppActivity>.empty().asBroadcastStream();
    }, brainId: brain);
    await tester.pumpWidget(forBrain('a'));
    await tester.pumpWidget(forBrain('a'));
    expect(opened, ['a']);
    await tester.pumpWidget(forBrain('b'));
    expect(opened, ['a', 'b']);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('the float rests after a few bobs instead of running forever', (
    tester,
  ) async {
    final feed = StreamController<AppActivity>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed, floating: true));
    await tester.pumpAndSettle();
    final rested = tester.getTopLeft(find.byType(InoPresence));
    feed.add(
      const AppActivity(app: 'assistant', state: 'listening', detail: ''),
    );
    await tester.pump();
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 1300));
    expect(tester.getTopLeft(find.byType(InoPresence)).dy, lessThan(rested.dy));
  });

  testWidgets(
    'a feed that answers then fails at once does not reset the pause',
    (tester) async {
      final feeds = <StreamController<AppActivity>>[];
      await tester.pumpWidget(
        hostWatch(() {
          final feed = StreamController<AppActivity>();
          feeds.add(feed);
          return feed.stream;
        }),
      );
      Future<void> answerThenFail() async {
        feeds.last.add(
          const AppActivity(app: 'assistant', state: 'idle', detail: ''),
        );
        await tester.pump();
        await feeds.last.close();
        await tester.pump();
      }

      await answerThenFail();
      await tester.pump(const Duration(seconds: 1));
      expect(feeds, hasLength(2));
      await answerThenFail();
      await tester.pump(const Duration(seconds: 1));
      expect(feeds, hasLength(2));
      await tester.pump(const Duration(seconds: 1));
      expect(feeds, hasLength(3));

      // A feed that stays open for a while is healthy again: the next loss waits the base pause.
      await tester.pump(const Duration(seconds: 6));
      await feeds.last.close();
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      expect(feeds, hasLength(4));
      await tester.pumpWidget(const SizedBox());
    },
  );
}
