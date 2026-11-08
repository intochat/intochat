import 'dart:async';

import 'package:intochat_os_ui/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('typing into an app field named by a key sends it to that '
      'textfield neuron', (tester) async {
    final changes = StreamController<void>.broadcast();
    addTearDown(changes.close);
    final sent = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'view',
            name: 'owner/apps/tasks/main',
            load: (kind, name) async => {
              'generation': 1,
              'version': 1,
              'root': {
                'kind': 2,
                'key': 'root',
                'children': [
                  {
                    'kind': 4,
                    'key': 'title',
                    'text': 'Title',
                    'detail': '{"_ui":"textfield","name":"owner/apps/tasks/title","value":"","placeholder":"Title","secret":false}',
                  },
                ],
              },
            },
            watch: (kind, name) => changes.stream,
            onAction: (event) async => sent.add(event),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField).first, 'Ship docs');
    await tester.pumpAndSettle();
    expect(sent.last, containsPair('kind', 'textfield'));
    expect(sent.last, containsPair('name', 'owner/apps/tasks/title'));
    expect(sent.last, containsPair('value', 'Ship docs'));
  });
}
