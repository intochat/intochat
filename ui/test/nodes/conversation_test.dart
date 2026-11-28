import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:gpt_markdown/gpt_markdown.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _view = 'local/apps/ino/view';

Map<String, dynamic> _published(
  Map<String, dynamic> root, {
  int generation = 3,
}) => {
  'generation': generation,
  'view': {'title': 'Ino', 'root': root},
};

Future<void> _host(
  WidgetTester tester,
  Map<String, dynamic> root, {
  List<Map<String, dynamic>>? events,
  Object? Function(Map<String, dynamic> event)? answer,
  VoiceTranscriber? transcribe,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.light('intochat'),
      home: Scaffold(
        body: ViewHost(
          kind: 'view',
          name: _view,
          overrides: ViewHostOverrides(
            knowsApp: (_) => false,
            load: (kind, name) async =>
                PublishedViewPayload.fromJson(_published(root)),
            watch: (kind, name) => const Stream.empty(),
            transcribe: transcribe,
            onAction: (event) async {
              final json = event.toJson();
              events?.add(json);
              return answer?.call(json);
            },
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Map<String, dynamic> _conversation({
  List<Map<String, dynamic>> messages = const [],
  String status = 'ready',
  Map<String, dynamic>? error,
  Map<String, dynamic>? stop,
  Map<String, dynamic>? waiting,
  Map<String, dynamic>? empty,
  bool enabled = true,
  bool? dictation,
}) => {
  'kind': 'conversation',
  'key': 'conversation',
  'messages': messages,
  'composer': {
    'key': 'composer',
    'onSubmit': {'target': '.', 'signal': 'send'},
    'placeholder': 'Ask anything.',
    'enabled': enabled,
    'dictation': ?dictation,
  },
  'waiting': waiting,
  'status': status,
  'error': error,
  'stop': stop,
  'empty': empty,
};

Map<String, dynamic> _message(
  String id,
  String role,
  String text, {
  String status = 'completed',
  List<Object>? cards,
  List<Object>? activities,
}) => {
  'id': id,
  'role': role,
  'text': text,
  'cards': cards,
  'activities': activities,
  'status': status,
  'model': null,
  'retry': null,
};

void main() {
  testWidgets('messages are drawn in order with their text and markdown', (
    tester,
  ) async {
    await _host(
      tester,
      _conversation(
        messages: [
          _message('m1', 'user', 'hello'),
          _message('m2', 'assistant', 'Hi **Vlad**.'),
        ],
      ),
    );
    expect(find.text('hello'), findsOneWidget);
    expect(find.byType(GptMarkdown), findsOneWidget);
    expect(
      tester.getTopLeft(find.text('hello')).dy,
      lessThan(tester.getTopLeft(find.byType(GptMarkdown)).dy),
    );
  });

  testWidgets('Return sends the text and clears the draft', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, _conversation(), events: events);
    await tester.tap(find.byType(TextField));
    await tester.pump();
    await tester.enterText(find.byType(TextField), 'What is new?');
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    expect(events.single, containsPair('kind', 'act'));
    expect(events.single, containsPair('signal', 'send'));
    expect(events.single, containsPair('value', 'What is new?'));
    expect(
      tester.widget<TextField>(find.byType(TextField)).controller!.text,
      isEmpty,
    );
  });

  testWidgets('Shift+Return adds a line and sends nothing', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, _conversation(), events: events);
    await tester.tap(find.byType(TextField));
    await tester.pump();
    await tester.enterText(find.byType(TextField), 'line one');
    await tester.sendKeyDownEvent(LogicalKeyboardKey.shiftLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.shiftLeft);
    await tester.pumpAndSettle();

    expect(events, isEmpty);
  });

  testWidgets('an empty draft sends nothing', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(tester, _conversation(), events: events);
    await tester.tap(find.byType(TextField));
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    expect(events, isEmpty);
  });

  // POST /act has no generation to be stale against: a refused send is sent
  // once, keeps the draft, and the host shows the refusal.
  testWidgets('a refused send is not repeated and the draft survives', (
    tester,
  ) async {
    final events = <Map<String, dynamic>>[];
    await _host(
      tester,
      _conversation(),
      events: events,
      answer: (event) => throw UiRequestException(
        'POST',
        '/act',
        409,
        '{"reason":"UnknownChild"}',
      ),
    );

    await tester.tap(find.byType(TextField));
    await tester.pump();
    await tester.enterText(find.byType(TextField), 'hello');
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pumpAndSettle();

    expect(events, hasLength(1));
    expect(events.single['kind'], 'act');
    expect(
      tester.widget<TextField>(find.byType(TextField)).controller!.text,
      'hello',
    );
  });

  testWidgets('Escape stops a running reply', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(
      tester,
      _conversation(
        status: 'working',
        stop: {
          'key': 'stop',
          'label': 'Stop',
          'command': {'target': '.', 'signal': 'stop'},
        },
        messages: [
          _message('m1', 'user', 'hello'),
          _message('m2', 'assistant', 'Hi', status: 'running'),
        ],
      ),
      events: events,
    );

    expect(find.text('Esc to stop'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(events.single, containsPair('signal', 'stop'));
  });

  testWidgets('a waiting card takes the place of the composer', (tester) async {
    await _host(
      tester,
      _conversation(
        waiting: {
          'kind': 'card',
          'title': 'Approve?',
          'body': 'Move the close date.',
          'children': <Object>[],
        },
      ),
    );

    expect(find.text('Approve?'), findsOneWidget);
    expect(find.byType(TextField), findsNothing);
  });

  testWidgets('a card of a message is drawn under it', (tester) async {
    await _host(
      tester,
      _conversation(
        messages: [
          _message(
            'm1',
            'assistant',
            'Here is the staged change.',
            cards: [
              {
                'kind': 'card',
                'title': 'Staged summary',
                'body': '1 commit verified',
                'children': <Object>[],
              },
            ],
          ),
        ],
      ),
    );

    expect(find.text('Staged summary'), findsOneWidget);
    expect(find.text('1 commit verified'), findsOneWidget);
  });

  testWidgets('the empty state is drawn when there are no messages', (
    tester,
  ) async {
    await _host(
      tester,
      _conversation(
        empty: {
          'title': 'What do you need?',
          'body': null,
          'suggestions': null,
        },
      ),
    );
    expect(find.text('What do you need?'), findsOneWidget);
  });

  testWidgets('the error line is drawn after the last message', (tester) async {
    await _host(
      tester,
      _conversation(
        messages: [_message('m1', 'user', 'hello')],
        status: 'failed',
        error: {
          'what': 'Ino could not answer.',
          'detail': null,
          'action': null,
        },
      ),
    );
    expect(find.text('hello'), findsOneWidget);
    expect(find.text('Ino could not answer.'), findsOneWidget);
  });

  testWidgets('a conversation fills a bounded box and does not overflow', (
    tester,
  ) async {
    await _host(
      tester,
      _conversation(
        messages: [
          for (var i = 0; i < 60; i++)
            _message('m$i', i.isEven ? 'user' : 'assistant', 'message $i'),
        ],
      ),
    );
    expect(tester.takeException(), isNull);
    expect(find.text('message 59'), findsOneWidget);
  });

  testWidgets(
    'an approval card inside a conversation view renders read-only without approve actions',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      await _host(
        tester,
        _conversation(
          messages: [
            _message(
              'm1',
              'assistant',
              'Here is the staged change.',
              cards: [
                {
                  'kind': 'approvalCard',
                  'key': 'approve-plan-1',
                  'id': 'plan-1',
                  'title': 'Push branch ino/fix',
                  'summary': '1 commit verified',
                  'rows': [
                    {'label': 'Target', 'oldValue': null, 'newValue': 'main'},
                  ],
                  'status': 'pending',
                  'resolve': {'planId': 'plan-1'},
                },
              ],
            ),
          ],
        ),
        events: events,
      );

      expect(find.text('Push branch ino/fix'), findsOneWidget);
      expect(find.text('Pending approval'), findsOneWidget);
      expect(find.text('Approve'), findsNothing);
      expect(find.text('Reject'), findsNothing);
      expect(events, isEmpty);
    },
  );

  testWidgets(
    'Enter over a non-empty composer draft sends nothing and approves nothing when a prompt arrives',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      var hasWaiting = false;
      final controller = StreamController<void>.broadcast();
      addTearDown(controller.close);

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.intochat(Brightness.light),
          home: Scaffold(
            body: ViewHost(
              kind: 'view',
              name: _view,
              overrides: ViewHostOverrides(
                knowsApp: (_) => false,
                load: (kind, name) async => PublishedViewPayload.fromJson({
                  'generation': hasWaiting ? 2 : 1,
                  'view': {
                    'title': 'Chat',
                    'root': _conversation(
                      waiting: hasWaiting
                          ? {
                              'kind': 'choiceCard',
                              'key': 'wait-choice',
                              'id': 'c-wait',
                              'question': 'Deploy staged branch?',
                              'options': [
                                {
                                  'title': 'Approve deploy',
                                  'pick': {
                                    'key': 'pick-approve',
                                    'label': 'Approve deploy',
                                    'command': {
                                      'target': '.',
                                      'signal': 'pick',
                                    },
                                  },
                                },
                                {
                                  'title': 'Cancel',
                                  'pick': {
                                    'key': 'pick-cancel',
                                    'label': 'Cancel',
                                    'command': {
                                      'target': '.',
                                      'signal': 'pick',
                                    },
                                  },
                                },
                              ],
                            }
                          : null,
                    ),
                  },
                }),
                watch: (kind, name) => controller.stream,
                onAction: (event) async {
                  events.add(event.toJson());
                  return null;
                },
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.byType(TextField));
      await tester.enterText(find.byType(TextField), 'Wait, let me finish');
      await tester.pump();

      hasWaiting = true;
      controller.add(null);
      await tester.pumpAndSettle();

      expect(find.text('Deploy staged branch?'), findsOneWidget);

      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();

      expect(events, isEmpty);
    },
  );

  testWidgets('no microphone when composer.dictation is false', (tester) async {
    await _host(
      tester,
      _conversation(dictation: false),
      transcribe: (_, _) async => 'ignored',
    );

    expect(find.byType(UiVoiceInput), findsNothing);
    expect(find.byIcon(Icons.mic_none_rounded), findsNothing);
    expect(find.byKey(const Key('workspace_voice')), findsNothing);
  });

  Future<void> pumpUntil(
    WidgetTester tester,
    bool Function() condition, {
    int maxSteps = 100,
  }) async {
    for (var i = 0; i < maxSteps; i++) {
      await tester.pump();
      if (condition()) return;
      await tester.runAsync(
        () => Future<void>.delayed(const Duration(milliseconds: 10)),
      );
    }
    await tester.pump();
  }

  testWidgets('a dictated transcript lands in the draft and is not sent', (
    tester,
  ) async {
    final tempDir = Directory.systemTemp.createTempSync('ino_voice_test_');
    addTearDown(() {
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });
    const recordChannel = MethodChannel('com.llfbandit.record/messages');
    const pathChannel = MethodChannel('plugins.flutter.io/path_provider');
    String? recordingPath;
    final eventChannels = <MethodChannel>[];
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

    messenger.setMockMethodCallHandler(pathChannel, (call) async {
      if (call.method == 'getTemporaryDirectory') {
        return tempDir.path;
      }
      return null;
    });
    messenger.setMockMethodCallHandler(recordChannel, (call) async {
      final args = call.arguments is Map
          ? Map<Object?, Object?>.from(call.arguments as Map)
          : const <Object?, Object?>{};
      switch (call.method) {
        case 'create':
          final recorderId = args['recorderId'] as String?;
          if (recorderId != null) {
            final eventChannel = MethodChannel(
              'com.llfbandit.record/events/$recorderId',
            );
            eventChannels.add(eventChannel);
            messenger.setMockMethodCallHandler(eventChannel, (_) async => null);
          }
          return null;
        case 'dispose':
        case 'cancel':
          return null;
        case 'hasPermission':
        case 'isEncoderSupported':
          return true;
        case 'start':
          recordingPath = args['path'] as String;
          File(recordingPath!).writeAsBytesSync(const [1, 2, 3, 4]);
          return null;
        case 'stop':
          return recordingPath;
      }
      return null;
    });
    addTearDown(() {
      messenger.setMockMethodCallHandler(recordChannel, null);
      messenger.setMockMethodCallHandler(pathChannel, null);
      for (final channel in eventChannels) {
        messenger.setMockMethodCallHandler(channel, null);
      }
    });

    final events = <Map<String, dynamic>>[];
    final transcriptions = <({Uint8List audio, String fileName})>[];
    await _host(
      tester,
      _conversation(dictation: true),
      events: events,
      transcribe: (audio, fileName) async {
        transcriptions.add((audio: audio, fileName: fileName));
        return transcriptions.length == 1
            ? 'check the release status'
            : 'please';
      },
    );

    expect(find.byType(UiVoiceInput), findsOneWidget);
    expect(find.byIcon(Icons.mic_none_rounded), findsOneWidget);

    await tester.tap(find.byKey(const Key('workspace_voice')));
    await pumpUntil(
      tester,
      () => find.byIcon(Icons.stop_circle_outlined).evaluate().isNotEmpty,
    );
    expect(find.byIcon(Icons.stop_circle_outlined), findsOneWidget);

    await tester.tap(find.byKey(const Key('workspace_voice')));
    await pumpUntil(
      tester,
      () => find.byIcon(Icons.mic_none_rounded).evaluate().isNotEmpty,
    );
    expect(find.byIcon(Icons.mic_none_rounded), findsOneWidget);

    expect(transcriptions, hasLength(1));
    expect(transcriptions.single.audio, const [1, 2, 3, 4]);
    expect(transcriptions.single.fileName, 'voice.wav');
    expect(
      tester.widget<TextField>(find.byType(TextField)).controller!.text,
      'check the release status',
    );
    expect(events, isEmpty);

    await tester.tap(find.byKey(const Key('workspace_voice')));
    await pumpUntil(
      tester,
      () => find.byIcon(Icons.stop_circle_outlined).evaluate().isNotEmpty,
    );
    expect(find.byIcon(Icons.stop_circle_outlined), findsOneWidget);

    await tester.tap(find.byKey(const Key('workspace_voice')));
    await pumpUntil(
      tester,
      () => find.byIcon(Icons.mic_none_rounded).evaluate().isNotEmpty,
    );
    expect(find.byIcon(Icons.mic_none_rounded), findsOneWidget);

    expect(
      tester.widget<TextField>(find.byType(TextField)).controller!.text,
      'check the release status please',
    );
    expect(events, isEmpty);

    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await pumpUntil(tester, () => events.isNotEmpty);

    expect(events.single, containsPair('signal', 'send'));
    expect(
      events.single,
      containsPair('value', 'check the release status please'),
    );
    expect(
      tester.widget<TextField>(find.byType(TextField)).controller!.text,
      isEmpty,
    );

    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pump();
    await tester.runAsync(
      () => Future<void>.delayed(const Duration(milliseconds: 20)),
    );
    await tester.pump();
  });
}
