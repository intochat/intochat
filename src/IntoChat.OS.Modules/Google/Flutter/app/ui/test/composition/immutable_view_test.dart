import 'dart:async';

import 'package:intochat_os_ui/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets(
    'inline tree has one read/watch and actions retain displayed generation',
    (tester) async {
      final changes = StreamController<void>.broadcast();
      final loads = <String>[];
      final watches = <String>[];
      final sent = <Map<String, dynamic>>[];
      var generation = 7;
      var version = 1;
      var active = true;
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: NeuronView(
              kind: 'view',
              name: 'calculator',
              load: (kind, name) async {
                loads.add('$kind:$name');
                return {
                  'generation': generation,
                  'version': version,
                  'root': {
                    'kind': 2,
                    'key': 'root',
                    'children': [
                      {'kind': 0, 'key': 'result', 'text': 'Result $version'},
                      {
                        'kind': 1,
                        'key': 'add',
                        'text': 'Add',
                        'action': 'add',
                        'enabled': active,
                      },
                    ],
                  },
                };
              },
              watch: (kind, name) {
                watches.add('$kind:$name');
                return changes.stream;
              },
              onAction: (event) async {
                sent.add(event);
              },
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(loads, ['view:calculator']);
      expect(watches, ['view:calculator']);
      final oldAction = tester
          .widget<TextButton>(find.widgetWithText(TextButton, 'Add'))
          .onPressed!;
      generation = 8;
      version = 2;
      changes.add(null);
      await tester.pumpAndSettle();
      expect(find.textContaining('Result 2'), findsOneWidget);
      oldAction();
      await tester.pumpAndSettle();
      expect(sent.single, containsPair('revision', 7));
      await tester.tap(find.text('Add'));
      await tester.pumpAndSettle();
      expect(sent.last, containsPair('revision', 8));
      expect(sent.last, containsPair('kind', 'view'));
      expect(sent.last, containsPair('name', 'calculator'));
      expect(sent.last, containsPair('field', 'add'));
      expect(sent.last, containsPair('action', 'add'));
      expect(
        sent.last['operationId'],
        matches(
          RegExp(
            r'^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$',
          ),
        ),
      );
      expect(sent.first['operationId'], isNot(sent.last['operationId']));
      active = false;
      changes.add(null);
      await tester.pumpAndSettle();
      expect(
        tester
            .widget<TextButton>(find.widgetWithText(TextButton, 'Add'))
            .onPressed,
        isNull,
      );
      expect(watches, ['view:calculator']);
      await tester.pumpWidget(const SizedBox());
      await changes.close();
    },
  );

  testWidgets(
    'keyed durable references preserve local state across sibling reorder',
    (tester) async {
      final changes = StreamController<void>.broadcast();
      var reverse = false;
      var referenceReads = 0;
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: NeuronView(
              kind: 'view',
              name: 'assistant',
              load: (kind, name) async {
                if (kind == 'textfield') {
                  referenceReads++;
                  return {
                    'definition': {'label': 'Draft', 'value': ''},
                  };
                }
                final children = [
                  {
                    'kind': 5,
                    'key': 'draft',
                    'reference': {'kind': 'textfield', 'name': 'draft'},
                  },
                  {'kind': 0, 'key': 'status', 'text': 'Ready'},
                ];
                return {
                  'generation': 1,
                  'version': 1,
                  'root': {
                    'kind': 2,
                    'key': 'root',
                    'children': reverse ? children.reversed.toList() : children,
                  },
                };
              },
              watch: (kind, name) =>
                  kind == 'view' ? changes.stream : const Stream.empty(),
              onAction: (_) async {
                throw StateError('offline');
              },
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      final state = tester.state(find.byType(TextField));
      final controller = tester
          .widget<TextField>(find.byType(TextField))
          .controller!;
      await tester.enterText(find.byType(TextField), 'unsent draft');
      await tester.pumpAndSettle();
      final readsBefore = referenceReads;
      reverse = true;
      changes.add(null);
      await tester.pumpAndSettle();
      expect(tester.state(find.byType(TextField)), same(state));
      expect(controller.text, 'unsent draft');
      expect(referenceReads, readsBefore);
      await tester.pumpWidget(const SizedBox());
      await changes.close();
    },
  );
}
