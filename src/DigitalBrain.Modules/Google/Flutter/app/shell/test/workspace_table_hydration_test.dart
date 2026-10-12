import 'dart:async';
import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import 'workspace_data_window_test.dart' show WorkspaceClient, tableJson;
import 'workspace_remote_controller_test.dart' show MemoryPersistence;

class HydrationClient extends WorkspaceClient {
  final navigation = <String>[];
  Map<String, dynamic> state = snapshot(1, 'table', true);

  static Map<String, dynamic> snapshot(
    int revision,
    String? focused,
    bool tableOpen,
  ) => {
    'revision': revision,
    'focusedWindow': focused,
    'home': {'kind': 'text', 'name': 'home'},
    'windows': [
      {
        'id': 'table',
        'title': 'Leads',
        'reference': {'kind': 'table', 'neuronId': 'table'},
        'isOpen': tableOpen,
      },
      if (focused == 'other')
        {
          'id': 'other',
          'title': 'Other document',
          'reference': {'kind': 'text', 'neuronId': 'other'},
          'isOpen': true,
        },
    ],
  };

  void change(String brain, String? focused, bool tableOpen) {
    state = snapshot(2, focused, tableOpen);
    events.add(
      utf8.encode('data: ${jsonEncode({'brainId': brain, 'revision': 2})}\n\n'),
    );
  }

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (request.method == 'POST' &&
        (request.url.path.contains('/windows/') ||
            request.url.path.endsWith('/focus'))) {
      navigation.add(request.url.path);
    }
    final root =
        request.url.pathSegments.length == 2 &&
        request.url.pathSegments.first == 'brains';
    Object? value;
    if (root || request.url.path.endsWith('/connected-sources')) value = state;
    if (request.url.path.endsWith('/apps/node')) {
      value = {'markdown': 'Current location'};
    }
    if (value == null) return super.send(request);
    return http.StreamedResponse(
      Stream.value(utf8.encode(jsonEncode(value))),
      200,
      headers: {'content-type': 'application/json'},
    );
  }
}

void main() {
  for (final scenario in [
    (null, false, 'closed table and Home'),
    (null, true, 'Home with table still open'),
    ('other', true, 'another focused document'),
  ]) {
    testWidgets('delayed table hydration respects ${scenario.$3}', (
      tester,
    ) async {
      final store = WorkspaceStore(persistence: MemoryPersistence());
      final api = HydrationClient();
      final pending = Completer<TableSnapshot>();
      var reads = 0;
      await tester.pumpWidget(
        WorkspaceApp(
          store: store,
          programmingClient: DigitalBrainUiClient(
            baseUri: Uri.parse('http://test'),
            httpClient: api,
          ),
          onReadTable: (brain, id, {offset = 0, limit = 50}) {
            expect(id, 'table');
            reads++;
            return pending.future;
          },
        ),
      );
      // Pump lifecycle frames without settling the intentionally pending spinner.
      for (var frame = 0; frame < 4; frame++) {
        await tester.pump();
      }
      expect(
        reads,
        1,
        reason: 'The real table editor must have started hydration',
      );
      expect(store.focusedWindow, 'table');
      api.change(store.currentProject.id, scenario.$1, scenario.$2);
      for (var frame = 0; frame < 4; frame++) {
        await tester.pump();
      }
      expect(store.remoteRevisions[store.currentProject.id], 2);
      expect(store.focusedWindow, scenario.$1);
      pending.complete(
        TableSnapshot.fromJson(tableJson(3, 'Hydrated company')),
      );
      await tester.pumpAndSettle();
      expect(store.focusedWindow, scenario.$1);
      expect(
        store.currentProject.presentation.openArtifactIds.contains('table'),
        scenario.$2,
      );
      expect(
        api.navigation,
        isEmpty,
        reason: 'Reading table contents cannot issue workspace navigation',
      );
      expect(
        store.currentProject.artifacts
            .singleWhere((a) => a.id == 'table')
            .data['rows'],
        isNotEmpty,
        reason: 'The delayed read was actually applied',
      );
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
      await api.events.close();
      store.dispose();
    });
  }
}
