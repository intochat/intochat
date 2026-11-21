import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

final _thread = <String, dynamic>{
  'kind': 'chatThreadView',
  'key': 'conversation',
  'messages': [
    {'id': 'u1', 'role': 'user', 'text': 'Hello'},
    {'id': 'a1', 'role': 'assistant', 'text': 'Hi there.'},
  ],
  'composer': {
    'key': 'composer',
    'onSubmit': {'owner': 'control.chatThread', 'signal': 'control.submitText'},
  },
  'selectModel': {
    'key': 'composer-model',
    'onSubmit': {
      'owner': 'control.chatThread',
      'signal': 'control.selectModel',
    },
  },
  'pinModel': {
    'key': 'composer-model-default',
    'onSubmit': {
      'owner': 'control.chatThread',
      'signal': 'control.selectModel',
      'args': {'default': true},
    },
  },
};

List<Map<String, dynamic>> _sampleModels({double? progressOnLocal}) => [
  {
    'id': 'qwen3.5:4b',
    'label': 'Qwen 3.5 4B',
    'section': 'local',
    'icon': 'provider:ollama',
    'abilities': ['tools', 'vision'],
    'available': true,
    'note': null,
    'progress': progressOnLocal,
  },
  {
    'id': 'qwen2.5-coder:14b',
    'label': 'Qwen 2.5 Coder 14B',
    'section': 'local',
    'icon': 'provider:ollama',
    'abilities': ['tools'],
    'available': true,
    'note': null,
    'progress': null,
  },
  {
    'id': 'gpt-5.4',
    'label': 'GPT-5.4',
    'section': 'cloud',
    'icon': 'provider:openai',
    'abilities': ['tools', 'vision', 'reasoning'],
    'available': true,
    'note': null,
    'progress': null,
  },
  {
    'id': 'gpt-5.4-nano',
    'label': 'GPT-5.4 Nano',
    'section': 'cloud',
    'icon': 'provider:openai',
    'abilities': ['tools'],
    'available': true,
    'note': null,
    'progress': null,
  },
  {
    'id': 'claude-opus-5',
    'label': 'Claude Opus 5',
    'section': 'cloud',
    'icon': 'provider:anthropic',
    'abilities': ['tools', 'vision', 'reasoning'],
    'available': false,
    'note': 'Add a key in API keys',
    'progress': null,
  },
];

Future<List<Map<String, dynamic>>> _pumpPicker(
  WidgetTester tester, {
  double? progressOnLocal,
}) async {
  tester.view.physicalSize = const Size(1200, 800);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.resetPhysicalSize);
  addTearDown(tester.view.resetDevicePixelRatio);

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
            slotProps: {
              'placeholder': 'Ask anything.',
              'activeModelId': 'qwen3.5:4b',
              'activeModelLabel': 'Qwen 3.5 4B',
              'activeProviderIcon': 'provider:ollama',
              'models': _sampleModels(progressOnLocal: progressOnLocal),
            },
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

Future<List<Map<String, dynamic>>> _openPicker(
  WidgetTester tester, {
  double? progressOnLocal,
}) async {
  final sent = await _pumpPicker(tester, progressOnLocal: progressOnLocal);
  await tester.tap(find.byKey(const ValueKey('composer-active-model')));
  await tester.pumpAndSettle();
  return sent;
}

void main() {
  testWidgets('the chip opens a popover with Local and Cloud sections', (
    tester,
  ) async {
    await _pumpPicker(tester);
    await tester.tap(find.byKey(const ValueKey('composer-active-model')));
    await tester.pumpAndSettle();

    expect(find.text('Local'), findsOneWidget);
    expect(find.text('Cloud'), findsOneWidget);
    expect(find.byKey(const ValueKey('model-row:qwen3.5:4b')), findsOneWidget);
    expect(find.byKey(const ValueKey('model-row:gpt-5.4')), findsOneWidget);
    expect(
      find.byKey(const ValueKey('model-row:claude-opus-5')),
      findsOneWidget,
    );
  });

  testWidgets('the ability filter hides rows without the ability', (
    tester,
  ) async {
    await _openPicker(tester);
    expect(
      find.byKey(const ValueKey('model-row:gpt-5.4-nano')),
      findsOneWidget,
    );

    await tester.tap(find.text('Vision'));
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('model-row:gpt-5.4-nano')), findsNothing);
    expect(
      find.byKey(const ValueKey('model-row:qwen2.5-coder:14b')),
      findsNothing,
    );
    expect(find.byKey(const ValueKey('model-row:qwen3.5:4b')), findsOneWidget);
    expect(find.byKey(const ValueKey('model-row:gpt-5.4')), findsOneWidget);
  });

  testWidgets('an unavailable row shows its reason and cannot be chosen', (
    tester,
  ) async {
    final sent = await _openPicker(tester);

    expect(find.text('Add a key in API keys'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('model-row:claude-opus-5')));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });

  testWidgets('choosing activates composer-model with the id', (tester) async {
    final sent = await _openPicker(tester);

    await tester.tap(find.byKey(const ValueKey('model-row:gpt-5.4')));
    await tester.pumpAndSettle();

    expect(sent, hasLength(1));
    expect(sent.single['kind'], 'view');
    expect(sent.single['key'], 'composer-model');
    expect(sent.single['value'], 'gpt-5.4');
  });

  testWidgets('the pin activates composer-model-default', (tester) async {
    final sent = await _openPicker(tester);

    await tester.tap(find.byKey(const ValueKey('model-pin:gpt-5.4')));
    await tester.pumpAndSettle();

    expect(sent, hasLength(1));
    expect(sent.single['kind'], 'view');
    expect(sent.single['key'], 'composer-model-default');
    expect(sent.single['value'], 'gpt-5.4');
  });

  testWidgets('a progress value draws a thin progress bar under the row', (
    tester,
  ) async {
    await _openPicker(tester, progressOnLocal: 0.45);

    final bar = find.descendant(
      of: find.byKey(const ValueKey('model-row:qwen3.5:4b')),
      matching: find.byType(LinearProgressIndicator),
    );
    expect(bar, findsOneWidget);
    expect(
      tester.widget<LinearProgressIndicator>(bar).value,
      closeTo(0.45, 0.001),
    );
  });

  testWidgets('picker trigger stays vertically centered with field and send', (
    tester,
  ) async {
    await _pumpPicker(tester);

    final y = [
      find.byType(TextField),
      find.byKey(const ValueKey('composer-active-model')),
      find.byKey(const ValueKey('composer-send')),
    ].map((f) => tester.getCenter(f).dy);
    expect(y.reduce(math.max) - y.reduce(math.min), lessThanOrEqualTo(1.0));
  });
}
