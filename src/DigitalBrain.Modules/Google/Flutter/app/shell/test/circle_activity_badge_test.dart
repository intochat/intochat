import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/circle_activity_badge.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

Widget host(
  StreamController<CircleTouched> feed, {
  bool homeShown = true,
  bool reduced = false,
}) => MaterialApp(
  home: MediaQuery(
    data: MediaQueryData(disableAnimations: reduced),
    child: Scaffold(
      body: Center(
        child: CircleActivityBadge(
          watch: () => feed.stream,
          homeShown: homeShown,
          child: const Icon(Icons.group, size: 48),
        ),
      ),
    ),
  ),
);

CircleTouched touch(String kind) =>
    CircleTouched(name: 'club', owner: 'ann', kind: kind);

String? count(WidgetTester tester) {
  final badge = find.byKey(const ValueKey('circle-badge'));
  if (badge.evaluate().isEmpty) return null;
  final scale = tester.widget<AnimatedScale>(
    find.ancestor(of: badge, matching: find.byType(AnimatedScale)),
  );
  if (scale.scale == 0) return null;
  return tester
      .widget<Text>(find.descendant(of: badge, matching: find.byType(Text)))
      .data;
}

void main() {
  testWidgets('spoke and invited touches are counted, others are not', (
    tester,
  ) async {
    final feed = StreamController<CircleTouched>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed));
    expect(count(tester), isNull);
    feed
      ..add(touch('spoke'))
      ..add(touch('invited'))
      ..add(touch('changed'))
      ..add(touch('left'));
    await tester.pumpAndSettle();
    expect(count(tester), '2');
  });

  testWidgets('the count is cleared once Home is hidden and keeps counting', (
    tester,
  ) async {
    final feed = StreamController<CircleTouched>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed, homeShown: false));
    feed.add(touch('spoke'));
    await tester.pumpAndSettle();
    expect(count(tester), '1');
    await tester.pumpWidget(host(feed, homeShown: true));
    expect(count(tester), '1');
    await tester.pumpWidget(host(feed, homeShown: false));
    await tester.pumpAndSettle();
    expect(count(tester), isNull);
    feed.add(touch('spoke'));
    await tester.pumpAndSettle();
    expect(count(tester), '1');
  });

  testWidgets('reduced motion shows the count without animating', (
    tester,
  ) async {
    final feed = StreamController<CircleTouched>();
    addTearDown(feed.close);
    await tester.pumpWidget(host(feed, reduced: true));
    feed.add(touch('invited'));
    await tester.pump();
    await tester.pump();
    expect(count(tester), '1');
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
