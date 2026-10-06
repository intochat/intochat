import 'package:digitalbrain_ui/src/composition/neuron_view.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('a table node renders its columns and rows', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: NeuronView(
          kind: 'table',
          name: 'w/apps/dv/grid',
          refreshEvery: const Duration(days: 1),
          load: (_, _) async => {
            'title': 'People',
            'columns': [
              {'id': 'c', 'title': 'Country'},
            ],
            'rows': [
              ['us'],
              ['uk'],
            ],
          },
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Country'), findsOneWidget);
    expect(find.text('uk'), findsOneWidget);
  });

  testWidgets('a chart node renders its title', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'chart',
            name: 'w/apps/dv/pie',
            refreshEvery: const Duration(days: 1),
            load: (_, _) async => {
              'title': 'Share',
              'kind': 'pie',
              'points': [
                {'eventId': '1', 'label': 'us', 'value': 2},
              ],
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('ui_chart_Share')), findsOneWidget);
  });
}
