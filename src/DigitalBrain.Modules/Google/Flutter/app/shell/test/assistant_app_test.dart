import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

import 'application_fixture.dart';

import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
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
      requests.any((path) => path.endsWith('/apps/assistant/request')),
      isTrue,
    );
    expect(requests.any((path) => path.contains('/agent')), isFalse);
    await tester.pumpWidget(const SizedBox());
    store.dispose();
  });

  testWidgets('opening an app posts a request and renders its surface neuron', (
    tester,
  ) async {
    final fixture = await ApplicationFixture.start(tester);

    await fixture.openApp('assistant');

    final request = fixture.server.requests.singleWhere(
      (request) => request.path.endsWith('/apps/assistant/request'),
    );
    expect(request.body['operation'], 'open');
    expect(fixture.renderedSurface, '${fixture.brainId}/apps/assistant/surface');
    expect(find.byType(NeuronView), findsWidgets);
    expect(find.byType(TextField), findsOneWidget);
    await fixture.dispose();
  });

  testWidgets('each Assistant launch posts a request and reveals its window', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final fixture = await ApplicationFixture.start(tester);

    await fixture.openApp('assistant');
    await fixture.openApp('assistant');

    final posted = fixture.server.requests
        .where((request) => request.path.endsWith('/apps/assistant/request'))
        .length;
    expect(posted, 2);
    expect(fixture.store.currentProject.artifacts.map((a) => a.id), [
      '${fixture.brainId}/apps/assistant/window',
    ]);
    expect(fixture.store.currentProject.presentation.windowModes, {
      '${fixture.brainId}/apps/assistant/window': 'floating',
    });
    expect(find.byType(TextField), findsOneWidget);
    expect(fixture.renderedSurface, '${fixture.brainId}/apps/assistant/surface');
    await fixture.dispose();
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
    expect(requests.any((path) => path.endsWith('/request')), isFalse);
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
