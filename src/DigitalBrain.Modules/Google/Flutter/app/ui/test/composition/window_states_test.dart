import 'dart:async';

import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

Future<void> pumpWindow(
  WidgetTester tester,
  Map<String, Object?> Function() load, {
  Future<void> Function(Map<String, dynamic>)? onAction,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: NeuronView(
          kind: 'surface',
          name: 'window',
          load: (_, _) async => load(),
          onAction: onAction,
        ),
      ),
    ),
  );
}

void main() {
  testWidgets('a previous component failure cannot replace current loading', (
    tester,
  ) async {
    final oldRead = Completer<Map<String, dynamic>>();
    final currentRead = Completer<Map<String, dynamic>>();
    Widget view(String name) => MaterialApp(
      home: Scaffold(
        body: NeuronView(
          kind: 'card',
          name: name,
          load: (_, name) =>
              name == 'old' ? oldRead.future : currentRead.future,
        ),
      ),
    );
    await tester.pumpWidget(view('old'));
    await tester.pumpWidget(view('current'));
    oldRead.completeError(StateError('Old component unavailable'));
    await tester.pump();
    expect(find.text('Could not load'), findsNothing);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    currentRead.complete({'title': 'Current component', 'body': 'Loaded'});
    await tester.pumpAndSettle();
    expect(find.text('Current component'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('retry shows loading until the requested component returns', (
    tester,
  ) async {
    final retry = Completer<Map<String, dynamic>>();
    var reads = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'card',
            name: 'retry',
            load: (_, _) {
              if (++reads == 1) return Future.error(StateError('Unavailable'));
              return retry.future;
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Could not load'), findsOneWidget);
    await tester.tap(find.text('Retry'));
    await tester.pump();
    expect(reads, 2);
    expect(find.text('Could not load'), findsNothing);
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    retry.complete({'title': 'Recovered', 'body': 'Available again'});
    await tester.pumpAndSettle();
    expect(find.text('Recovered'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('an empty window renders its declared state', (tester) async {
    await pumpWindow(tester, () => {'state': 'empty'});
    await tester.pumpAndSettle();
    expect(find.text('Nothing here yet'), findsWidgets);
    expect(find.text('This window has no content yet.'), findsOneWidget);
  });

  testWidgets('a denied read renders permission denied', (tester) async {
    await pumpWindow(tester, () => {'state': 'permissionDenied'});
    await tester.pumpAndSettle();
    expect(find.text('Not available to you'), findsOneWidget);
    expect(
      find.text('You do not have permission to view this window.'),
      findsOneWidget,
    );
  });

  testWidgets('an expired connection renders its reconnect state', (
    tester,
  ) async {
    await pumpWindow(tester, () => {'state': 'expiredConnection'});
    await tester.pumpAndSettle();
    expect(find.text('Connection expired'), findsOneWidget);
    expect(find.textContaining('Reconnect'), findsOneWidget);
  });

  testWidgets('a failed read renders a retry', (tester) async {
    await pumpWindow(tester, () => {'state': 'failed', 'message': 'Boom.'});
    await tester.pumpAndSettle();
    expect(find.text('Could not load'), findsOneWidget);
    expect(find.text('Boom.'), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
  });

  testWidgets('a throwing loader renders the failed state', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'surface',
            name: 'window',
            load: (_, _) async => throw StateError('nope'),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Could not load'), findsOneWidget);
    expect(find.text('This component could not be loaded.'), findsOneWidget);
  });

  testWidgets('first-run renders the assistant and matching starter prompts', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: FirstRunView(
            state: FirstRunState.fromMetadata({
              'assistantId': 'intocaht',
              'assistantTitle': 'IntoChat',
              'prompts': [
                {
                  'id': 'assistant',
                  'label': 'Ask the assistant',
                  'prompt': 'Ask the assistant something.',
                  'source': 'assistant',
                },
                {
                  'id': 'salesforce',
                  'label': 'Review my Salesforce leads',
                  'prompt': 'Review my leads.',
                  'source': 'salesforce',
                },
              ],
            }),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('IntoChat'), findsOneWidget);
    expect(find.text('Ask the assistant'), findsOneWidget);
    expect(find.text('Review my Salesforce leads'), findsOneWidget);
    expect(find.text('salesforce'), findsOneWidget);
  });

  testWidgets('an unknown kind renders the declared fallback, never blank', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'not-a-kind',
            name: 'window',
            load: (_, _) async => {'definition': <String, Object?>{}},
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(
      find.text('${RendererRegistry.fallbackLabel}: not-a-kind'),
      findsOneWidget,
    );
    expect(find.text(RendererRegistry.fallbackExplanation), findsOneWidget);
  });
}
