import 'dart:async';
import 'dart:convert';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  testWidgets('the application tree chooses its declared renderer', (
    tester,
  ) async {
    final starts = <String>[];
    var conversation = false;
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        final workspace = request.url.pathSegments[1];
        final response = {
          'generation': 1,
          'view': {
            'title': workspace,
            'root': conversation
                ? {
                    'kind': 'card',
                    'title': 'Research companion',
                    'body': '',
                    'children': <Object>[],
                  }
                : {'kind': 'text', 'value': 'The application selected text'},
          },
        };
        return http.Response(jsonEncode(response), 200);
      }),
    );
    Widget view(String workspace) => MaterialApp(
      home: Scaffold(
        body: ApplicationSurface(
          knowsApp: (app) => app == 'ino',
          client: client,
          workspace: workspace,
          application: 'assistant',
          start: () async {
            starts.add(workspace);
            return {'kind': 'view', 'name': '$workspace/root'};
          },
        ),
      ),
    );

    await tester.pumpWidget(view('one'));
    await tester.pumpAndSettle();
    expect(find.text('The application selected text'), findsOneWidget);
    expect(find.textContaining('Research companion'), findsNothing);

    conversation = true;
    await tester.pumpWidget(view('two'));
    await tester.pumpAndSettle();
    expect(find.text('Research companion'), findsOneWidget);
    expect(find.text('The application selected text'), findsNothing);
    expect(starts, ['one', 'two']);

    await tester.pumpWidget(view('two'));
    await tester.pumpAndSettle();
    expect(starts, ['one', 'two']);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a failed application start can be retried', (tester) async {
    var attempts = 0;
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        return http.Response(
          jsonEncode({
            'generation': 1,
            'view': {
              'title': 'Ready',
              'root': {'kind': 'text', 'value': 'Ready'},
            },
          }),
          200,
        );
      }),
    );
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ApplicationSurface(
            knowsApp: (app) => app == 'ino',
            client: client,
            workspace: 'one',
            application: 'assistant',
            start: () async {
              if (++attempts == 1) throw StateError('Unavailable');
              return {'kind': 'view', 'name': 'ready'};
            },
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Retry application'), findsOneWidget);
    await tester.tap(find.text('Retry application'));
    await tester.pumpAndSettle();
    expect(find.text('Ready'), findsOneWidget);
    expect(attempts, 2);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets(
    'ApplicationSurface subscribes IScreen views to DigitalBrain.IScreen ViewChanged via NeuronWatchMux and coalesces mid-flight pushes',
    (tester) async {
      var activeStage = 'plan';
      var generation = 0;
      var nodeReads = 0;
      Completer<void>? holdNextRead;
      final link = _ScreenWatchTestLink();

      final client = IntoChatOSUiClient(
        baseUri: Uri.parse('http://test'),
        httpClient: MockClient((request) async {
          nodeReads++;
          if (holdNextRead != null) {
            final barrier = holdNextRead!;
            holdNextRead = null;
            await barrier.future;
          }
          generation++;
          return http.Response(
            jsonEncode({
              'alias': 'app.engineer',
              'generation': generation,
              'view': {
                'title': 'IntoChat Engineer',
                'acts': 'act',
                'root': {
                  'kind': 'column',
                  'children': [
                    {'kind': 'text', 'value': 'Stage:$activeStage'},
                  ],
                },
              },
            }),
            200,
          );
        }),
      )..link = link;
      addTearDown(client.close);

      await tester.pumpWidget(
        MaterialApp(
          theme: ThemeData(
            useMaterial3: true,
            extensions: [
              IcColors.forPreset(IcStylePreset.warmPaper, dark: false),
            ],
          ),
          home: Scaffold(
            body: ApplicationSurface(
              knowsApp: (_) => true,
              client: client,
              workspace: 'owner',
              application: 'engineer',
              start: () async => {
                'kind': 'view',
                'name': 'owner/apps/engineer/view',
              },
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      // Allow NeuronWatchMux's 50ms chunk debounce to open the Watch stream.
      await tester.pump(const Duration(milliseconds: 60));

      expect(find.text('Stage:plan'), findsOneWidget);
      expect(nodeReads, 1);
      expect(
        link.openedSources.last.any(
          (s) =>
              s.contract == 'DigitalBrain.IScreen' &&
              s.key == 'owner/app.engineer/default' &&
              s.types.length == 1 &&
              s.types.single == 'ViewChanged',
        ),
        isTrue,
      );

      // A state change without a ViewChanged push: no timer reloads the IScreen.
      activeStage = 'verify';
      await tester.pump(const Duration(milliseconds: 500));
      expect(nodeReads, 1);
      expect(find.text('Stage:plan'), findsOneWidget);

      // Hold reload #2 in flight and emit a second ViewChanged push mid-reload.
      final gate = Completer<void>();
      holdNextRead = gate;
      link.emitScreenChanged('owner/app.engineer/default', sequence: 2);
      await tester.pump();
      expect(nodeReads, 2);

      // Second ViewChanged arrives while reload #2 is still in flight.
      activeStage = 'pr';
      link.emitScreenChanged('owner/app.engineer/default', sequence: 3);
      await tester.pump();
      expect(nodeReads, 2);

      // Release reload #2 -> ViewHost completes #2 and immediately fetches #3 ('Stage:pr').
      gate.complete();
      await tester.pumpAndSettle();
      expect(nodeReads, 3);
      expect(find.text('Stage:pr'), findsOneWidget);

      // Dispose ApplicationSurface: NeuronWatchMux closes its stream with zero leaks.
      await tester.pumpWidget(const SizedBox());
      await tester.pump();
      expect(client.watches('owner').openStreams, 0);
      expect(link.activeStreams, 0);
    },
  );
}

final class _ScreenWatchTestLink implements NeuronLink {
  final openedSources = <List<WatchSourceSpec>>[];
  int activeStreams = 0;
  Future<Map<String, dynamic>?> Function(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments,
  )?
  onCall;

  final _events = StreamController<NeuronSignal>.broadcast();

  void emitScreenChanged(String key, {required int sequence}) {
    _events.add(
      NeuronSignal(
        source: key,
        type: 'ViewChanged',
        publisher: key,
        sequence: sequence,
        payload: {'alias': 'app.engineer', 'generation': sequence},
        serverTimeMs: 0,
      ),
    );
  }

  @override
  Stream<NeuronSignal> watch(String brainId, List<WatchSourceSpec> sources) {
    openedSources.add(List.unmodifiable(sources));
    late StreamController<NeuronSignal> controller;
    StreamSubscription<NeuronSignal>? sub;
    controller = StreamController<NeuronSignal>(
      onListen: () {
        activeStreams++;
        sub = _events.stream.listen(
          controller.add,
          onError: controller.addError,
          onDone: controller.close,
        );
      },
      onCancel: () async {
        activeStreams--;
        await sub?.cancel();
      },
    );
    return controller.stream;
  }

  @override
  Future<Map<String, dynamic>?> call(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments,
  ) async => onCall?.call(brainId, contract, key, method, arguments);

  @override
  Future<dynamic> readState(
    String brainId,
    String contract,
    String key, {
    Duration? timeout,
  }) async => null;

  @override
  Future<void> shutdown() async {
    await _events.close();
  }
}
