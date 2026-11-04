import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';
import 'package:intochat_os_ui/src/components/graph/ui_graph.dart';

void main() {
  testWidgets('UiGraph renders title, nodes, and property inspector', (
    tester,
  ) async {
    const part = UiGraphPart(
      title: 'Neural Network',
      nodes: [
        UiGraphNode(
          id: 'n1',
          label: 'Node Alpha',
          type: 'input',
          weight: 0.85,
          properties: {'activation': 'relu', 'layer': '0'},
        ),
        UiGraphNode(
          id: 'n2',
          label: 'Node Beta',
          type: 'hidden',
          weight: 0.92,
          properties: {'activation': 'sigmoid', 'layer': '1'},
        ),
      ],
      edges: [
        UiGraphEdge(
          id: 'e1',
          source: 'n1',
          target: 'n2',
          relationship: 'connects_to',
          weight: 0.75,
        ),
      ],
    );

    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(body: UiGraph(part: part)),
      ),
    );
    await tester.pumpAndSettle();

    // Verify header
    expect(find.text('Neural Network'), findsOneWidget);
    expect(find.text('2 nodes · 1 edges'), findsOneWidget);

    // Verify initial inspector display (first node: Node Alpha)
    expect(find.text('Node Alpha'), findsWidgets);
    expect(find.text('activation: '), findsOneWidget);
    expect(find.text('relu'), findsOneWidget);

    // Tap on Node Beta to select it
    await tester.tap(find.text('Node Beta').first);
    await tester.pumpAndSettle();

    // Verify inspector updated to Node Beta
    expect(find.text('Node Beta'), findsWidgets);
    expect(find.text('sigmoid'), findsOneWidget);
  });

  testWidgets('NeuronView renders graph kind without fallback', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: ThemeData(extensions: const [UiColors.intochatLight]),
        home: Scaffold(
          body: NeuronView(
            key: UniqueKey(),
            kind: 'graph',
            name: 'knowledge_graph',
            load: (k, name) async => {
              'version': 1,
              'definition': {
                'title': 'Test Graph',
                'nodes': [
                  {'id': 'a', 'label': 'Entity A', 'type': 'service'},
                ],
                'edges': [],
              },
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('Test Graph'), findsOneWidget);
    expect(find.text(RendererRegistry.fallbackLabel), findsNothing);
  });
}
