import 'dart:async';

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
  int Function()? generationOf,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.light('intochat'),
      home: Scaffold(
        body: ViewHost(
          knowsApp: (_) => false,
          kind: 'view',
          name: _view,
          load: (kind, name) async =>
              _published(root, generation: generationOf?.call() ?? 3),
          watch: (kind, name) => const Stream.empty(),
          onAction: (event) async {
            events?.add(event);
            return answer?.call(event);
          },
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
}) => {
  'kind': 'conversation',
  'key': 'conversation',
  'messages': messages,
  'composer': {
    'key': 'composer',
    'onSubmit': {'owner': 'app.ino', 'signal': 'send'},
    'placeholder': 'Ask anything.',
    'enabled': enabled,
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

    expect(events.single, containsPair('key', 'composer'));
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

  testWidgets(
    'a send refused once as stale retries with reloaded generation and clears',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      var gen = 3;
      var firstAttempt = true;
      await _host(
        tester,
        _conversation(),
        events: events,
        generationOf: () => gen,
        answer: (event) {
          if (firstAttempt) {
            firstAttempt = false;
            gen = 4;
            throw UiRequestException(
              'POST',
              '/apps/event',
              409,
              '{"status":"stale"}',
            );
          }
          return null;
        },
      );

      await tester.tap(find.byType(TextField));
      await tester.pump();
      await tester.enterText(find.byType(TextField), 'hello');
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();

      expect(events, hasLength(2));
      expect(events[0]['generation'], 3);
      expect(events[1]['generation'], 4);
      expect(
        tester.widget<TextField>(find.byType(TextField)).controller!.text,
        isEmpty,
      );
      expect(find.byType(SnackBar), findsNothing);
    },
  );

  testWidgets(
    'a send refused as stale is sent once more and the draft survives a second refusal',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      var refusals = 2;
      await _host(
        tester,
        _conversation(),
        events: events,
        answer: (event) {
          if (refusals > 0) {
            refusals--;
            throw UiRequestException(
              'POST',
              '/apps/event',
              409,
              '{"status":"stale"}',
            );
          }
          return null;
        },
      );

      await tester.tap(find.byType(TextField));
      await tester.pump();
      await tester.enterText(find.byType(TextField), 'hello');
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();

      expect(events, hasLength(2));
      expect(
        tester.widget<TextField>(find.byType(TextField)).controller!.text,
        'hello',
      );
      expect(find.byType(SnackBar), findsNothing);
    },
  );

  testWidgets('Escape stops a running reply', (tester) async {
    final events = <Map<String, dynamic>>[];
    await _host(
      tester,
      _conversation(
        status: 'working',
        stop: {
          'key': 'stop',
          'label': 'Stop',
          'emit': {'owner': 'app.ino', 'signal': 'stop'},
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
    expect(events.single, containsPair('key', 'stop'));
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
              knowsApp: (_) => false,
              kind: 'view',
              name: _view,
              load: (kind, name) async => {
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
                                  'emit': {'owner': 'app.ino', 'signal': 'pick'},
                                },
                              },
                              {
                                'title': 'Cancel',
                                'pick': {
                                  'key': 'pick-cancel',
                                  'label': 'Cancel',
                                  'emit': {'owner': 'app.ino', 'signal': 'pick'},
                                },
                              },
                            ],
                          }
                        : null,
                  ),
                },
              },
              watch: (kind, name) => controller.stream,
              onAction: (event) async {
                events.add(event);
                return null;
              },
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
}
