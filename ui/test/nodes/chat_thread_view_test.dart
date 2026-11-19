import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

Map<String, dynamic> _button(
  String key,
  String label,
  String signal, [
  Map<String, dynamic>? args,
]) => {
  'kind': 'button',
  'key': key,
  'label': label,
  'command': {'target': '.', 'signal': signal, 'args': ?args},
};

final _thread = {
  'kind': 'chatThreadView',
  'key': 'conversation',
  'messages': [
    {'id': 'u1', 'role': 'user', 'text': 'First question'},
    {
      'id': 'a1',
      'role': 'assistant',
      'text': '',
      'status': 'failed',
      'retry': _button('retry:a1', 'Try again', 'control.retry', {'id': 'a1'}),
      'error': {'kind': 'errorLine', 'what': 'The model did not answer.'},
    },
    {'id': 'u2', 'role': 'user', 'text': 'Second question'},
    {'id': 'a2', 'role': 'assistant', 'text': 'An answer.'},
  ],
  'composer': {
    'key': 'composer',
    'onSubmit': {'owner': 'control.chatThread', 'signal': 'control.submitText'},
  },
  'stop': _button('stop', 'Stop', 'control.stop'),
  'retry': _button('error-action', 'Try again', 'control.retry', {'id': ''}),
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props,
) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: NodeRenderer.render(
          Node.fromJson(_thread),
          NodeContext(
            view: 'local/apps/ino/slots/conversation',
            generation: 3,
            actNeuron: 'local/apps/ino',
            slotPath: 'conversation',
            slotProps: props,
            send: (event) async {
              sent.add(Map<String, dynamic>.from(event));
              return {'delivered': true};
            },
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return sent;
}

void main() {
  testWidgets('a failed message draws its reason and a retry', (tester) async {
    await _pump(tester, {'placeholder': 'Ask anything.', 'status': 'failed'});

    expect(find.text('The model did not answer.'), findsOneWidget);
    expect(find.text('Try again'), findsOneWidget);
    expect(find.text('An answer.'), findsOneWidget);
  });

  testWidgets('the retry posts the thread\'s retry node for that message', (
    tester,
  ) async {
    final sent = await _pump(tester, {'placeholder': 'Ask anything.'});

    final node = Node.fromJson(_thread) as ChatThreadViewNode;
    final retry = node.messages[1].retry!;
    expect(retry.command!.signal, 'control.retry');
    expect(retry.command!.args, {'id': 'a1'});

    await tester.tap(find.text('Try again'));
    await tester.pumpAndSettle();

    expect(sent.single, {
      'kind': 'act',
      'neuron': 'local/apps/ino',
      'target': 'conversation',
      'signal': 'control.retry',
      'args': {'id': 'a1'},
    });
  });

  testWidgets('props draw the placeholder, the stop and an error to open', (
    tester,
  ) async {
    await _pump(tester, {
      'placeholder': 'Ask anything.',
      'working': true,
      'error': {
        'what': 'Ino has no model to answer with.',
        'actionLabel': 'Set up a model',
        'open': 'os://window/settings?section=models',
        'retry': false,
      },
    });

    expect(find.text('Ask anything.'), findsOneWidget);
    expect(find.text('Ino has no model to answer with.'), findsOneWidget);
    expect(find.text('Set up a model'), findsOneWidget);
    expect(find.text('Esc to stop'), findsOneWidget);
  });
}
