import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

Map<String, dynamic> _chatThreadRoot() => {
  'kind': 'chatThreadView',
  'key': 'conversation',
  'messages': [
    {'id': 'u1', 'role': 'user', 'text': 'Hello Ino'},
    {'id': 'a1', 'role': 'assistant', 'text': 'Hi! How can I help?'},
  ],
  'composer': {
    'key': 'composer',
    'onSubmit': {'target': '.', 'signal': 'control.submitText'},
  },
};

Future<void> _pumpHost(
  WidgetTester tester, {
  required String kind,
  required String name,
  required NeuronLoader load,
}) async {
  tester.view.physicalSize = const Size(1200, 900);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: SizedBox(
          height: 900,
          child: ViewHost(
            knowsApp: (_) => false,
            kind: kind,
            name: name,
            watch: (_, _) => const Stream.empty(),
            onAction: (_) async => null,
            load: load,
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
    'column-wrapped thread slot fills host height and docks composer at bottom',
    (tester) async {
      await _pumpHost(
        tester,
        kind: 'view',
        name: 'local/apps/ino/view',
        load: (kind, name) async => switch (name) {
          'local/apps/ino/view' => {
            'generation': 1,
            'view': {
              'title': 'Ino',
              'root': {
                'kind': 'column',
                'extents': [1],
                'children': [
                  {
                    'kind': 'neuronRef',
                    'refKind': 'child',
                    'neuronUri': 'ino/thread',
                  },
                ],
              },
            },
          },
          'ino/thread' => {
            'generation': 1,
            'view': {'title': 'Ino', 'root': _chatThreadRoot()},
          },
          _ => throw StateError('Unexpected load $kind:$name'),
        },
      );

      expect(find.text('Hello Ino'), findsOneWidget);
      expect(find.text('Hi! How can I help?'), findsOneWidget);

      final send = find.byKey(const ValueKey('composer-send'));
      expect(send, findsOneWidget);
      expect(tester.getBottomLeft(send).dy, greaterThanOrEqualTo(900 - 40));
      expect(
        find.ancestor(
          of: find.byType(TextField),
          matching: find.byType(SingleChildScrollView),
        ),
        findsNothing,
      );
    },
  );

  testWidgets(
    'bare chatThreadView slot root fills host height without SingleChildScrollView',
    (tester) async {
      await _pumpHost(
        tester,
        kind: 'child',
        name: 'local/apps/ino/children/conversation',
        load: (kind, name) async => switch (name) {
          'local/apps/ino/children/conversation' => {
            'generation': 1,
            'view': {'title': 'Ino', 'root': _chatThreadRoot()},
          },
          _ => throw StateError('Unexpected load $kind:$name'),
        },
      );

      expect(find.text('Hello Ino'), findsOneWidget);
      expect(find.text('Hi! How can I help?'), findsOneWidget);

      final send = find.byKey(const ValueKey('composer-send'));
      expect(send, findsOneWidget);
      expect(tester.getBottomLeft(send).dy, greaterThanOrEqualTo(900 - 40));
      expect(
        find.ancestor(
          of: find.byType(TextField),
          matching: find.byType(SingleChildScrollView),
        ),
        findsNothing,
      );
      expect(NodeRenderer.fills(Node.fromJson(_chatThreadRoot())), isTrue);
    },
  );
}
