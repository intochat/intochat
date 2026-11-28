import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _conversationContract =
    'IntoChat.OS.Modules.Flutter.Conversations.IConversation';

Map<String, dynamic> _delta(
  String entry,
  String delta,
  int at, {
  bool mapEntry = false,
}) => {
  'id': 'c-1',
  'entry': mapEntry ? {'value': entry} : entry,
  'delta': delta,
  'at': at,
};

Map<String, dynamic> _chatThreadViewJson({
  required String entry,
  String replyText = '',
  String status = 'running',
}) => {
  'generation': 1,
  'view': {
    'title': 'Ino',
    'root': {
      'kind': 'chatThreadView',
      'key': 'conversation',
      'messages': [
        {'id': 'u1', 'role': 'user', 'text': 'go', 'status': 'completed'},
        {'id': entry, 'role': 'assistant', 'text': replyText, 'status': status},
      ],
      'composer': {
        'key': 'composer',
        'onSubmit': {'target': '.', 'signal': 'control.submitText'},
      },
    },
  },
};

void main() {
  testWidgets('200 deltas grow the pending reply with no reload and no read', (
    tester,
  ) async {
    final deltas = StreamController<Map<String, dynamic>>.broadcast();
    addTearDown(deltas.close);
    var loads = 0;
    var reads = 0;
    final watched = <(String, String, String)>[];

    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            kind: 'child',
            name: 'local/apps/ino/children/conversation',
            childProps: const {
              'placeholder': 'Ask anything.',
              'conversation': 'conversations/c-1',
            },
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              watchSignal: (contract, key, signal) {
              watched.add((contract, key, signal));
              return deltas.stream;
            },
              callNeuron: (contract, key, method, args) async {
              reads++;
              return null;
            },
              onAction: (_) async => null,
              load: (kind, name) async {
              loads++;
              return PublishedViewPayload.fromJson(
                _chatThreadViewJson(entry: 'r1/reply'),
              );
            },
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    var at = 0;
    for (var i = 0; i < 200; i++) {
      final chunk = 'w$i ';
      deltas.add(_delta('r1/reply', chunk, at));
      at += chunk.length;
    }
    await tester.pumpAndSettle();

    expect(find.textContaining('w0 w1 w2'), findsOneWidget);
    expect(find.textContaining('w198 w199'), findsOneWidget);
    expect(loads, 1);
    expect(reads, 0);
    expect(watched, [
      (_conversationContract, 'conversations/c-1', 'TextDelta'),
    ]);
  });

  testWidgets('a gap reads the conversation once and resumes', (tester) async {
    final deltas = StreamController<Map<String, dynamic>>.broadcast();
    addTearDown(deltas.close);
    var reads = 0;
    final calls = <(String, String, String, List<Object?>)>[];

    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            kind: 'child',
            name: 'local/apps/ino/children/conversation',
            childProps: const {
              'placeholder': 'Ask anything.',
              'conversation': 'conversations/c-1',
            },
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              watchSignal: (_, _, _) => deltas.stream,
              callNeuron: (contract, key, method, args) async {
              reads++;
              calls.add((contract, key, method, args));
              return {
                'active': {'runId': 'r1', 'reply': 'hello '},
              };
            },
              onAction: (_) async => null,
              load: (kind, name) async => PublishedViewPayload.fromJson(
              _chatThreadViewJson(entry: 'r1/reply'),
            ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    deltas
      ..add(_delta('r1/reply', 'world', 6))
      ..add(_delta('r1/reply', '!', 11));
    await tester.pumpAndSettle();

    expect(find.textContaining('hello world!'), findsOneWidget);
    expect(reads, 1);
    expect(calls, [
      (
        _conversationContract,
        'conversations/c-1',
        'Read',
        const <Object?>[null],
      ),
    ]);
  });

  testWidgets('an overlapping delta is not doubled', (tester) async {
    final deltas = StreamController<Map<String, dynamic>>.broadcast();
    addTearDown(deltas.close);

    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            kind: 'child',
            name: 'local/apps/ino/children/conversation',
            childProps: const {
              'placeholder': 'Ask anything.',
              'conversation': 'conversations/c-1',
            },
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              watchSignal: (_, _, _) => deltas.stream,
              onAction: (_) async => null,
              load: (kind, name) async => PublishedViewPayload.fromJson(
              _chatThreadViewJson(entry: 'r1/reply'),
            ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    // Accepts both plain String entry and {value: ...} object entry.
    deltas
      ..add(_delta('r1/reply', 'ab', 0))
      ..add(_delta('r1/reply', 'bc', 1, mapEntry: true))
      ..add(_delta('r1/reply', 'ab', 0));
    await tester.pumpAndSettle();

    expect(find.textContaining('abc'), findsOneWidget);
    expect(find.textContaining('abbc'), findsNothing);
  });

  testWidgets('the stored reply replaces the pending text', (tester) async {
    final deltas = StreamController<Map<String, dynamic>>.broadcast();
    final viewWatch = StreamController<void>.broadcast();
    addTearDown(deltas.close);
    addTearDown(viewWatch.close);

    var completed = false;
    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            kind: 'child',
            name: 'local/apps/ino/children/conversation',
            childProps: const {
              'placeholder': 'Ask anything.',
              'conversation': 'conversations/c-1',
            },
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => viewWatch.stream,
              watchSignal: (_, _, _) => deltas.stream,
              onAction: (_) async => null,
              load: (kind, name) async => PublishedViewPayload.fromJson(
              completed
                  ? _chatThreadViewJson(
                      entry: 'r1/reply',
                      replyText: 'final',
                      status: 'completed',
                    )
                  : _chatThreadViewJson(entry: 'r1/reply'),
            ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    deltas.add(_delta('r1/reply', 'streaming pending draft', 0));
    await tester.pumpAndSettle();
    expect(find.textContaining('streaming pending draft'), findsOneWidget);

    completed = true;
    viewWatch.add(null);
    await tester.pumpAndSettle();

    expect(find.textContaining('final'), findsOneWidget);
    expect(find.textContaining('streaming pending draft'), findsNothing);

    // A late delta arriving after completion does not resurrect pending text.
    deltas.add(_delta('r1/reply', ' late', 23));
    await tester.pumpAndSettle();
    expect(find.textContaining('late'), findsNothing);
  });

  testWidgets('no conversation prop, no watch', (tester) async {
    var watchCalls = 0;

    await tester.pumpWidget(
      MaterialApp(
        theme: UiTheme.intochat(Brightness.light),
        home: Scaffold(
          body: ViewHost(
            kind: 'child',
            name: 'local/apps/ino/children/conversation',
            childProps: const {'placeholder': 'Ask anything.'},
            overrides: ViewHostOverrides(
              knowsApp: (_) => false,
              watch: (_, _) => const Stream.empty(),
              watchSignal: (_, _, _) {
              watchCalls++;
              return const Stream.empty();
            },
              onAction: (_) async => null,
              load: (kind, name) async => PublishedViewPayload.fromJson(
              _chatThreadViewJson(entry: 'r1/reply'),
            ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(watchCalls, 0);
  });

  testWidgets(
    'nested slot ViewHost inherits watchSignal and callNeuron from parent ViewHost',
    (tester) async {
      final deltas = StreamController<Map<String, dynamic>>.broadcast();
      addTearDown(deltas.close);
      var reads = 0;

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.intochat(Brightness.light),
          home: Scaffold(
            body: ViewHost(
              kind: 'view',
              name: 'local/apps/ino/view',
              overrides: ViewHostOverrides(
                knowsApp: (_) => false,
                watch: (_, _) => const Stream.empty(),
                watchSignal: (_, _, _) => deltas.stream,
                callNeuron: (_, _, _, _) async {
                reads++;
                return {
                  'active': {'runId': 'r1', 'reply': 'nested '},
                };
              },
                onAction: (_) async => null,
                load: (kind, name) async =>
                  PublishedViewPayload.fromJson(switch (name) {
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
                              'neuronUri': 'conversation',
                              'props': {
                                'placeholder': 'Ask anything.',
                                'conversation': 'conversations/c-nested',
                              },
                            },
                          ],
                        },
                      },
                    },
                    'local/apps/ino/children/conversation' =>
                      _chatThreadViewJson(entry: 'r1/reply'),
                    _ => throw StateError('Unexpected load $kind:$name'),
                  }),
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      deltas.add(_delta('r1/reply', 'slot!', 7));
      await tester.pumpAndSettle();

      expect(find.textContaining('nested slot!'), findsOneWidget);
      expect(reads, 1);
    },
  );
}
