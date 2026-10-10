import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/brain_graph_panel.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/brain_replay.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/home_screen.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

JournalSignal _signal(
  int sequence,
  String type,
  Map<String, dynamic> payload,
) => JournalSignal(
  sequence: sequence,
  type: type,
  publisher: 'p',
  payload: payload,
);

final _journal = [
  _signal(1, 'Created', {'Owner': 'o'}),
  _signal(2, 'InstanceCreated', {'Type': 'chat', 'Key': 'b1/chats/main'}),
  _signal(3, 'AppChanged', {'Name': 'notes', 'Version': '1', 'Status': 'Live'}),
  _signal(4, 'AppPublished', {'Name': 'notes', 'Version': '1'}),
  _signal(5, 'InstanceCreated', {'Type': 'app', 'Key': 'b1/apps/notes'}),
  _signal(6, 'AppChanged', {'Name': 'files', 'Version': '1', 'Status': 'Live'}),
  _signal(7, 'SomethingNew', {'x': 1}),
];

// Pages of the fake journal exactly as the endpoint cuts them.
Future<SignalsPage> _page(int since, int limit) async {
  final rest = _journal.where((s) => s.sequence > since).take(limit).toList();
  return SignalsPage(
    next: rest.isEmpty ? since : rest.last.sequence,
    signals: rest,
  );
}

BrainSnapshot _snapshot(List<String> ids) => BrainSnapshot(
  rootId: 'r',
  observedAt: DateTime.utc(2026),
  nodes: [
    for (final id in ids)
      BrainNeuron(id: id, type: 'x', name: id, label: id, module: 'm'),
  ],
);

Widget _host(Widget child, {bool reduced = false}) => MediaQuery(
  data: MediaQueryData(disableAnimations: reduced),
  child: MaterialApp(
    home: UiThemeScope(child: Scaffold(body: child)),
  ),
);

Finder _entry(String id) => find.byKey(ValueKey('enter_$id'));

void main() {
  test('growth keeps the neurons and apps and skips every unknown signal', () {
    final growth = Growth();
    _journal.forEach(growth.add);

    expect(
      [
        for (var i = 0; i < growth.frames; i++)
          growth.frame(i).nodes.map((n) => n.id).toList(),
      ],
      [
        <String>[],
        ['b1/chats/main'],
        ['b1/chats/main', 'apps/notes'],
        ['b1/chats/main', 'apps/notes', 'apps/files'],
      ],
    );
  });

  testWidgets('the first page starts the replay before the rest has arrived', (
    tester,
  ) async {
    final second = Completer<SignalsPage>();
    final frames = <int?>[];
    Future<SignalsPage> load(int since, int limit) =>
        since == 0 ? _page(0, 3) : second.future;
    await tester.pumpWidget(
      _host(
        BrainReplayBar(
          load: load,
          pageSize: 3,
          onFrame: (f) => frames.add(f?.nodes.length),
        ),
      ),
    );
    await tester.tap(find.byKey(const ValueKey('brain-replay-start')));
    await tester.pump();
    await tester.pump();

    expect(frames, [0]);
    await tester.pump(const Duration(milliseconds: 450));
    expect(frames, [0, 1]);
    await tester.pump(const Duration(milliseconds: 450));
    expect(frames, [0, 1, 2]);
    // The player waits at the loaded end instead of going live.
    await tester.pump(const Duration(milliseconds: 900));
    expect(frames.last, 2);
    expect(find.byKey(const ValueKey('brain-replay-live')), findsOneWidget);

    second.complete(_page(3, 3).then((p) => p));
    await tester.pump(const Duration(milliseconds: 450));
    expect(frames.last, 3);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a failed page keeps the loaded frames and retry resumes', (
    tester,
  ) async {
    var failing = true;
    final frames = <int?>[];
    Future<SignalsPage> load(int since, int limit) async {
      if (since > 0 && failing) throw StateError('journal unavailable');
      return _page(since, 3);
    }

    await tester.pumpWidget(
      _host(
        BrainReplayBar(
          load: load,
          pageSize: 3,
          onFrame: (f) => frames.add(f?.nodes.length),
        ),
      ),
    );
    await tester.tap(find.byKey(const ValueKey('brain-replay-start')));
    await tester.pump();
    await tester.pump();

    expect(find.byKey(const ValueKey('brain-replay-retry')), findsOneWidget);
    expect(find.byKey(const ValueKey('brain-replay-scrub')), findsOneWidget);
    expect(frames, [0]);

    failing = false;
    await tester.tap(find.byKey(const ValueKey('brain-replay-retry')));
    await tester.pump();
    await tester.pump();

    expect(find.byKey(const ValueKey('brain-replay-retry')), findsNothing);
    await tester.pump(const Duration(milliseconds: 450 * 4));
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('brain-replay-is-live')), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a new neuron in the live feed enters the graph by diff', (
    tester,
  ) async {
    final feed = StreamController<BrainSnapshot>();
    await tester.pumpWidget(_host(BrainGraphPanel(watch: () => feed.stream)));
    feed.add(_snapshot(['a']));
    await tester.pump();
    final first = tester.state(find.byType(LumenBrainGraph));

    feed.add(_snapshot(['a', 'b']));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 60));

    expect(tester.state(find.byType(LumenBrainGraph)), same(first));
    expect(
      tester
          .widget<Opacity>(
            find.descendant(of: _entry('b'), matching: find.byType(Opacity)),
          )
          .opacity,
      lessThan(1),
    );
    await tester.pumpAndSettle();
    await tester.pumpWidget(const SizedBox());
    unawaited(feed.close());
  });

  testWidgets(
    'scrubbing replays the growth from the journal then the live feed resumes',
    (tester) async {
      final feed = StreamController<BrainSnapshot>();
      await tester.pumpWidget(
        _host(BrainGraphPanel(watch: () => feed.stream, loadSignals: _page)),
      );
      feed.add(_snapshot(['live-1', 'live-2']));
      await tester.pump();
      expect(_entry('live-1'), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('brain-replay-start')));
      await tester.pump();
      await tester.pump();
      // The replay starts from the empty brain, not from the live snapshot.
      expect(_entry('live-1'), findsNothing);
      expect(find.byKey(const ValueKey('brain-replay-live')), findsOneWidget);

      await tester.pump(const Duration(milliseconds: 450));
      expect(_entry('b1/chats/main'), findsOneWidget);
      await tester.pump(const Duration(milliseconds: 450));
      expect(_entry('apps/notes'), findsOneWidget);

      // Playing through the end hands the graph back to the live feed.
      await tester.pump(const Duration(milliseconds: 450));
      await tester.pumpAndSettle();
      expect(_entry('apps/files'), findsNothing);
      expect(_entry('live-1'), findsOneWidget);
      expect(
        find.byKey(const ValueKey('brain-replay-is-live')),
        findsOneWidget,
      );

      // Dragging the scrub bar back shows a past graph again.
      await tester.drag(
        find.byKey(const ValueKey('brain-replay-scrub')),
        const Offset(-2000, 0),
      );
      await tester.pumpAndSettle();
      expect(_entry('live-1'), findsNothing);
      expect(find.byKey(const ValueKey('brain-replay-live')), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('brain-replay-live')));
      await tester.pumpAndSettle();
      expect(_entry('live-1'), findsOneWidget);

      await tester.pumpWidget(const SizedBox());
      unawaited(feed.close());
    },
  );

  testWidgets('reduced motion jumps the replay to the live feed', (
    tester,
  ) async {
    final feed = StreamController<BrainSnapshot>();
    await tester.pumpWidget(
      _host(
        BrainGraphPanel(watch: () => feed.stream, loadSignals: _page),
        reduced: true,
      ),
    );
    feed.add(_snapshot(['live-1']));
    await tester.pump();

    await tester.tap(find.byKey(const ValueKey('brain-replay-start')));
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('brain-replay-is-live')), findsOneWidget);
    expect(_entry('live-1'), findsOneWidget);

    await tester.pumpWidget(const SizedBox());
    unawaited(feed.close());
  });

  testWidgets('home draws the brain only when it is given the feed', (
    tester,
  ) async {
    await tester.pumpWidget(
      _host(HomeScreen(workspaceId: 'b1', onOpen: (_) async {})),
    );
    expect(find.byKey(const ValueKey('home-brain')), findsNothing);

    final feed = StreamController<BrainSnapshot>();
    await tester.pumpWidget(
      _host(
        HomeScreen(
          workspaceId: 'b1',
          onOpen: (_) async {},
          watchBrain: () => feed.stream,
          loadSignals: _page,
        ),
      ),
    );
    feed.add(_snapshot(['a']));
    await tester.pump();

    expect(find.byKey(const ValueKey('home-brain')), findsOneWidget);
    expect(_entry('a'), findsOneWidget);
    expect(find.byKey(const ValueKey('brain-replay-start')), findsOneWidget);

    await tester.pumpWidget(const SizedBox());
    unawaited(feed.close());
  });
}
