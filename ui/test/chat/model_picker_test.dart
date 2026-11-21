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

List<Map<String, dynamic>> _sampleModels({
  double? progressOnLocal,
  Map<String, double>? progress,
}) => [
  {
    'id': 'qwen3.5:4b',
    'label': 'Qwen 3.5 4B',
    'section': 'local',
    'icon': 'provider:ollama',
    'abilities': ['tools', 'vision'],
    'available': true,
    'note': null,
    'progress': progress?['qwen3.5:4b'] ?? progressOnLocal,
  },
  {
    'id': 'qwen2.5-coder:14b',
    'label': 'Qwen 2.5 Coder 14B',
    'section': 'local',
    'icon': 'provider:ollama',
    'abilities': ['tools'],
    'available': true,
    'note': null,
    'progress': progress?['qwen2.5-coder:14b'],
  },
  {
    'id': 'gemma4:e2b',
    'label': 'Gemma 4 E2B',
    'section': 'local',
    'icon': 'provider:ollama',
    'abilities': ['tools', 'vision'],
    'available': false,
    'note': progress?['gemma4:e2b'] != null
        ? 'Downloading ${(progress!['gemma4:e2b']! * 100).round()}%'
        : 'Install · 2.5 GB · Good',
    'progress': progress?['gemma4:e2b'],
  },
  {
    'id': 'huge-local:70b',
    'label': 'Huge Local 70B',
    'section': 'local',
    'icon': 'provider:ollama',
    'abilities': ['tools'],
    'available': false,
    'note': 'Too big for this device',
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
  Map<String, double>? progress,
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
              'models': _sampleModels(
                progressOnLocal: progressOnLocal,
                progress: progress,
              ),
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
  Map<String, double>? progress,
}) async {
  final sent = await _pumpPicker(
    tester,
    progressOnLocal: progressOnLocal,
    progress: progress,
  );
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

    final row = find.byKey(const ValueKey('model-row:claude-opus-5'));
    await tester.ensureVisible(row);
    await tester.pumpAndSettle();
    expect(find.text('Add a key in API keys'), findsOneWidget);
    await tester.tap(row);
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

  testWidgets('a downloading row draws a progress bar', (tester) async {
    await _openPicker(tester, progress: {'gemma4:e2b': 0.42});

    final bar = find.descendant(
      of: find.byKey(const ValueKey('model-row:gemma4:e2b')),
      matching: find.byType(LinearProgressIndicator),
    );
    expect(bar, findsOneWidget);
    expect(
      tester.widget<LinearProgressIndicator>(bar).value,
      closeTo(0.42, 0.001),
    );
  });

  testWidgets('an installable row shows an install button that activates composer-model', (
    tester,
  ) async {
    final sent = await _openPicker(tester);

    final installButton = find.byKey(const ValueKey('model-install:gemma4:e2b'));
    expect(installButton, findsOneWidget);
    await tester.tap(installButton);
    await tester.pumpAndSettle();

    expect(sent, hasLength(1));
    expect(sent.single['kind'], 'view');
    expect(sent.single['key'], 'composer-model');
    expect(sent.single['value'], 'gemma4:e2b');
  });

  testWidgets('a too big model row shows no install button and cannot be chosen', (
    tester,
  ) async {
    final sent = await _openPicker(tester);

    expect(find.byKey(const ValueKey('model-install:huge-local:70b')), findsNothing);
    expect(find.text('Too big for this device'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('model-row:huge-local:70b')));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
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
