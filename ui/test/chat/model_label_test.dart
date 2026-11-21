import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/src/chat/model_label.dart';

void main() {
  test('a device model names its device', () {
    expect(
      modelLabel('device:vlad-pc/qwen2.5-coder:14b', {
        'qwen2.5-coder:14b': 'Qwen 2.5 Coder 14B',
      }),
      'Qwen 2.5 Coder 14B · vlad-pc',
    );
    expect(
      modelLabel('deepseek-v4.1-flash', {
        'deepseek-v4.1-flash': 'DeepSeek V4.1 Flash',
      }),
      'DeepSeek V4.1 Flash',
    );
  });

  test('unknown inner model id keeps the raw model id with device name', () {
    expect(
      modelLabel('device:vlad-pc/custom-model:latest', const {}),
      'custom-model:latest · vlad-pc',
    );
  });

  test('malformed device ids are not rewritten', () {
    expect(modelLabel('device:/x', const {}), 'device:/x');
    expect(modelLabel('device:a b/x', const {}), 'device:a b/x');
    expect(modelLabel('devic:a/x', const {}), 'devic:a/x');
  });

  testWidgets('message model chip formats device model ids', (tester) async {
    final thread = Node.fromJson({
      'kind': 'chatThreadView',
      'key': 'conversation',
      'messages': [
        {
          'id': 'm1',
          'role': 'assistant',
          'text': 'hi',
          'status': 'completed',
          'model': 'device:vlad-pc/qwen2.5-coder:14b',
        },
      ],
      'composer': {
        'key': 'composer',
        'onSubmit': {
          'owner': 'control.chatThread',
          'signal': 'control.submitText',
        },
      },
    });

    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: NodeRenderer.render(
            thread,
            NodeContext(
              view: 'local/apps/ino/slots/conversation',
              generation: 1,
              actNeuron: 'local/apps/ino',
              slotPath: 'conversation',
              slotProps: const {
                'models': [
                  {
                    'id': 'device:vlad-pc/qwen2.5-coder:14b',
                    'label': 'Qwen 2.5 Coder 14B',
                    'icon': 'provider:device',
                    'section': 'local',
                  },
                ],
              },
              send: (_) async => {'delivered': true},
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    final chip = find.byKey(const ValueKey('msg-model:m1'));
    expect(chip, findsOneWidget);
    expect(
      find.descendant(
        of: chip,
        matching: find.text('Qwen 2.5 Coder 14B · vlad-pc'),
      ),
      findsOneWidget,
    );
  });
}
