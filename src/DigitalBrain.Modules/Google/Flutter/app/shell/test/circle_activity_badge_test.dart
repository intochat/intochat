import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/circle_activity_badge.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

Widget host(
  CircleActivityFeed feed, {
  bool homeShown = true,
  bool reduced = false,
}) => MaterialApp(
  home: MediaQuery(
    data: MediaQueryData(disableAnimations: reduced),
    child: Scaffold(
      body: Center(
        child: CircleActivityBadge(
          feed: feed,
          homeShown: homeShown,
          child: const Icon(Icons.group, size: 48),
        ),
      ),
    ),
  ),
);

CircleTouched touch(String kind, [String name = 'club']) =>
    CircleTouched(name: name, owner: 'ann', kind: kind);

String? shown(WidgetTester tester) {
  final badge = find.byKey(const ValueKey('circle-badge'));
  final scale = tester.widget<AnimatedScale>(
    find.ancestor(of: badge, matching: find.byType(AnimatedScale)),
  );
  if (scale.scale == 0) return null;
  return tester
      .widget<Text>(find.descendant(of: badge, matching: find.byType(Text)))
      .data;
}

void main() {
  late StreamController<CircleTouched> live;
  late List<StreamController<CircleTouched>> connections;
  late List<Completer<void>> pauses;

  CircleActivityFeed feed() {
    connections = [];
    pauses = [];
    return CircleActivityFeed(
      () {
        live = StreamController<CircleTouched>();
        connections.add(live);
        return live.stream;
      },
      pause: (_) {
        final pause = Completer<void>();
        pauses.add(pause);
        return pause.future;
      },
    );
  }

  testWidgets('spoke and invited touches are counted, others are not', (
    tester,
  ) async {
    final f = feed();
    addTearDown(f.dispose);
    await tester.pumpWidget(host(f));
    expect(shown(tester), isNull);
    live
      ..add(touch('spoke'))
      ..add(touch('invited', 'other'))
      ..add(touch('changed'))
      ..add(touch('left'));
    await tester.pumpAndSettle();
    expect(shown(tester), '2');
  });

  testWidgets('the count survives the badge unmounting, then Home clears it', (
    tester,
  ) async {
    final f = feed();
    addTearDown(f.dispose);
    live.add(touch('spoke'));
    await tester.pump();
    expect(f.count, 1);
    await tester.pumpWidget(host(f));
    expect(shown(tester), '1');
    await tester.pumpWidget(const SizedBox());
    expect(f.count, 0);
    live.add(touch('spoke', 'b'));
    await tester.pump();
    expect(f.count, 1);
    await tester.pumpWidget(host(f));
    expect(shown(tester), '1');
  });

  testWidgets('hiding Home while mounted clears the count', (tester) async {
    final f = feed();
    addTearDown(f.dispose);
    await tester.pumpWidget(host(f));
    live.add(touch('spoke'));
    await tester.pump();
    await tester.pumpWidget(host(f, homeShown: false));
    await tester.pumpAndSettle();
    expect(shown(tester), isNull);
  });

  testWidgets(
    'a dropped stream reconnects after a pause and a replay is not news',
    (tester) async {
      final f = feed();
      addTearDown(f.dispose);
      live.add(touch('spoke'));
      await tester.pump();
      expect(f.count, 1);
      await live.close();
      await tester.pump();
      expect(pauses, hasLength(1));
      expect(connections, hasLength(1));
      pauses.single.complete();
      await tester.pump();
      expect(connections, hasLength(2));
      live.add(touch('spoke'));
      await tester.pump();
      expect(f.count, 1);
      live.add(touch('spoke'));
      await tester.pump();
      expect(f.count, 2);
      live.add(touch('invited', 'new'));
      await tester.pump();
      expect(f.count, 3);
    },
  );

  testWidgets('a stream error also reconnects', (tester) async {
    final f = feed();
    addTearDown(f.dispose);
    live.addError('boom');
    await tester.pump();
    expect(pauses, hasLength(1));
    pauses.single.complete();
    await tester.pump();
    expect(connections, hasLength(2));
  });

  testWidgets('reduced motion shows the count without animating', (
    tester,
  ) async {
    final f = feed();
    addTearDown(f.dispose);
    await tester.pumpWidget(host(f, reduced: true));
    live.add(touch('invited'));
    await tester.pump();
    await tester.pump();
    expect(shown(tester), '1');
  });

  test('CircleTouched reads the live payload', () {
    final touched = CircleTouched.fromJson({
      'name': 'club',
      'owner': 'ann',
      'kind': 'spoke',
    });
    expect(
      (touched.name, touched.owner, touched.kind),
      ('club', 'ann', 'spoke'),
    );
  });
}
