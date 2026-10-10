import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

BrainNeuron _node(String id) =>
    BrainNeuron(id: id, type: 'x', name: id, label: id, module: 'm');

BrainSnapshot _snapshot(
  List<String> ids, [
  List<BrainSynapse> edges = const [],
]) => BrainSnapshot(
  rootId: 'r',
  observedAt: DateTime.utc(2026),
  nodes: [for (final id in ids) _node(id)],
  synapses: edges,
);

double _opacity(WidgetTester tester, String id) => tester
    .widget<Opacity>(
      find.descendant(
        of: find.byKey(ValueKey('enter_$id')),
        matching: find.byType(Opacity),
      ),
    )
    .opacity;

Widget _host(BrainSnapshot snapshot, {bool reduced = false}) => MediaQuery(
  data: MediaQueryData(disableAnimations: reduced),
  child: MaterialApp(
    home: UiThemeScope(
      child: Scaffold(
        body: SizedBox(
          width: 900,
          height: 600,
          child: LumenBrainGraph(
            snapshot: snapshot,
            onNeuron: (_) {},
            onSynapse: (_) {},
          ),
        ),
      ),
    ),
  ),
);

void main() {
  testWidgets('a new neuron enters with an animation, existing ones do not', (
    tester,
  ) async {
    await tester.pumpWidget(_host(_snapshot(['a'])));
    expect(_opacity(tester, 'a'), 1);
    await tester.pumpWidget(_host(_snapshot(['a', 'b'])));
    await tester.pump(const Duration(milliseconds: 60));
    expect(_opacity(tester, 'b'), lessThan(1));
    expect(_opacity(tester, 'a'), 1);
    await tester.pumpAndSettle();
    expect(_opacity(tester, 'b'), 1);
  });

  testWidgets('a new synapse enters with an animation', (tester) async {
    const edge = BrainSynapse(
      id: 'e1',
      sourceId: 'a',
      targetId: 'b',
      signalType: 'S',
      kind: 'Declared',
    );
    await tester.pumpWidget(_host(_snapshot(['a', 'b'])));
    await tester.pumpWidget(_host(_snapshot(['a', 'b'], [edge])));
    await tester.pump(const Duration(milliseconds: 60));
    expect(_opacity(tester, 'e1'), lessThan(1));
    await tester.pumpAndSettle();
    expect(_opacity(tester, 'e1'), 1);
  });

  testWidgets('reduced motion shows a new neuron at once', (tester) async {
    await tester.pumpWidget(_host(_snapshot(['a']), reduced: true));
    await tester.pumpWidget(_host(_snapshot(['a', 'b']), reduced: true));
    await tester.pump();
    expect(_opacity(tester, 'b'), 1);
  });

  testWidgets('inserting a neuron mid-list keeps every other tile mounted', (
    tester,
  ) async {
    await tester.pumpWidget(_host(_snapshot(['a', 'c'])));
    final before = [
      tester.element(find.byKey(const ValueKey('neuron_a'))),
      tester.element(find.byKey(const ValueKey('neuron_c'))),
    ];
    await tester.pumpWidget(_host(_snapshot(['a', 'b', 'c'])));
    await tester.pump(const Duration(milliseconds: 60));
    expect(
      tester.element(find.byKey(const ValueKey('neuron_a'))),
      same(before[0]),
    );
    expect(
      tester.element(find.byKey(const ValueKey('neuron_c'))),
      same(before[1]),
    );
    expect(_opacity(tester, 'c'), 1);
    await tester.pumpAndSettle();
  });
}
