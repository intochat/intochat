import 'dart:async';
import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'application_fixture.dart';

import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:flutter/material.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/home_screen.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('main workspace mounts only the neuron-declared application', (
    tester,
  ) async {
    final store = WorkspaceStore(persistence: MemoryWorkspacePersistence());
    final requests = <String>[];
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        requests.add(request.url.path);
        if (request.url.path.endsWith('/apps')) {
          return http.Response(jsonEncode([assistantSummary]), 200);
        }
        return http.Response(
          jsonEncode(
            applicationResponse(request) ??
                {'revision': 0, 'windows': [], 'apps': []},
          ),
          200,
        );
      }),
    );
    await tester.pumpWidget(
      WorkspaceApp(store: store, programmingClient: client),
    );
    await tester.pumpAndSettle();
    expect(find.byType(ApplicationSurface), findsOneWidget);
    expect(find.byType(TextField), findsOneWidget);
    expect(
      requests.any((path) => path.endsWith('/apps/assistant/ask')),
      isTrue,
    );
    expect(requests.any((path) => path.contains('/agent')), isFalse);
    await tester.pumpWidget(const SizedBox());
    store.dispose();
  });

  testWidgets('each Assistant launch opens a fresh floating neuron surface', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final store = WorkspaceStore(persistence: MemoryWorkspacePersistence());
    var opened = 0;
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        if (request.url.path.endsWith('/apps')) {
          return http.Response(jsonEncode([assistantSummary]), 200);
        }
        if (request.url.path.endsWith('/apps/assistant/ask') &&
            (jsonDecode(request.body) as Map)['input'] != 'docked') {
          final id = 'window-${++opened}';
          return http.Response(
            jsonEncode({
              'id': id,
              'title': 'Assistant $opened',
              'kind': 'surface',
              'surface': {'kind': 'surface', 'name': id},
            }),
            200,
          );
        }
        return http.Response(
          jsonEncode(
            applicationResponse(request) ?? {'revision': 0, 'windows': []},
          ),
          200,
        );
      }),
    );
    await tester.pumpWidget(
      WorkspaceApp(store: store, programmingClient: client),
    );
    await tester.pumpAndSettle();
    final open = tester.widget<HomeScreen>(find.byType(HomeScreen)).onOpen;
    for (var i = 0; i < 2; i++) {
      unawaited(open('assistant'));
      await tester.pumpAndSettle();
    }
    expect(opened, 2);
    expect(store.currentProject.artifacts.map((a) => a.id), [
      'window-1',
      'window-2',
    ]);
    expect(store.currentProject.presentation.windowModes, {
      'window-1': 'floating',
      'window-2': 'floating',
    });
    expect(find.byType(TextField), findsNWidgets(3));
    await tester.pumpWidget(const SizedBox());
    store.dispose();
  });

  testWidgets('nothing docked means no side panel', (tester) async {
    final store = WorkspaceStore(persistence: MemoryWorkspacePersistence())
      ..settings.dock = '';
    final requests = <String>[];
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        requests.add(request.url.path);
        if (request.url.path.endsWith('/apps')) {
          return http.Response(jsonEncode([assistantSummary]), 200);
        }
        return http.Response(
          jsonEncode({'revision': 0, 'windows': [], 'apps': []}),
          200,
        );
      }),
    );
    await tester.pumpWidget(
      WorkspaceApp(store: store, programmingClient: client),
    );
    await tester.pumpAndSettle();
    expect(find.byType(ApplicationSurface), findsNothing);
    expect(find.byTooltip('Side panel'), findsNothing);
    expect(requests.any((path) => path.endsWith('/ask')), isFalse);
    await tester.pumpWidget(const SizedBox());
    store.dispose();
  });

  test('the docked app survives a reload', () {
    final prefs = WorkspacePreferences()..dock = 'customer-researcher';
    expect(
      WorkspacePreferences.fromJson(prefs.toJson()).dock,
      'customer-researcher',
    );
    expect(WorkspacePreferences.fromJson(const {}).dock, 'assistant');
  });
}
