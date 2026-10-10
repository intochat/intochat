import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/brain_graph_panel.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

Widget host(Stream<BrainSnapshot> Function() watch) => MaterialApp(
  home: MediaQuery(
    data: const MediaQueryData(disableAnimations: true),
    child: Scaffold(body: BrainGraphPanel(watch: watch)),
  ),
);

void main() {
  testWidgets(
    'a brain feed that fails is reopened with a growing pause that a short-lived feed does not reset',
    (tester) async {
      var opened = 0;
      final feeds = <StreamController<BrainSnapshot>>[];
      await tester.pumpWidget(
        host(() {
          opened++;
          if (opened == 1) {
            return Stream<BrainSnapshot>.error(StateError('down'));
          }
          final feed = StreamController<BrainSnapshot>();
          feeds.add(feed);
          return feed.stream;
        }),
      );
      await tester.pump();
      expect(find.byKey(const ValueKey('brain-graph-error')), findsOneWidget);
      expect(opened, 1);

      await tester.pump(const Duration(seconds: 1));
      expect(opened, 2);
      feeds.single.add(
        BrainSnapshot(rootId: 'b', observedAt: DateTime.utc(2026, 10, 10)),
      );
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const ValueKey('brain-graph-error')), findsNothing);

      // The feed ending cleanly is a loss too: it is reopened after a pause, not left silent.
      await feeds.single.close();
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      expect(opened, 2);
      await tester.pump(const Duration(seconds: 1));
      expect(opened, 3);
      await tester.pumpWidget(const SizedBox());
    },
  );
}
