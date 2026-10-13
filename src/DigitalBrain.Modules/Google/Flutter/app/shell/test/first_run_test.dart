import 'dart:async';
import 'dart:convert';

import 'application_fixture.dart';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import 'workspace_remote_controller_test.dart' show MemoryPersistence;

class FirstRunClient extends http.BaseClient {
  bool home = false;
  final events = StreamController<List<int>>.broadcast();

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    // The Home graph's brain feed is not served here: it shows its error instead of spinning forever.
    if (request.url.path.endsWith('/brain/events')) {
      return http.StreamedResponse(const Stream.empty(), 404);
    }
    if (request.url.path.endsWith('/events')) {
      return http.StreamedResponse(events.stream, 200);
    }
    if (request.url.path.endsWith('/home')) home = true;
    final Object value;
    final application = applicationResponse(request);
    if (request.url.path.endsWith('/apps/assistant')) {
      value = assistantView;
    } else if (request.url.path.endsWith('/apps/ino')) {
      value = {
        'manifest': {
          'title': 'Ino',
          'entry': {'kind': 'text', 'name': 'welcome'},
        },
      };
    } else if (application != null) {
      value = application;
    } else if (request.url.path.endsWith('/marketplace')) {
      value = const <Object>[];
    } else if (request.url.path.endsWith('/apps')) {
      value = const [assistantSummary];
    } else if (request.url.path.contains('/conversations/')) {
      value = {'revision': 1, 'activeRun': null, 'runs': <Object>[]};
    } else {
      value = {
        'revision': 0,
        'path': [
          request.url.pathSegments[1],
          if (!home) '${request.url.pathSegments[1]}/apps/ino',
        ],
        'open': ['${request.url.pathSegments[1]}/apps/ino'],
      };
    }
    return http.StreamedResponse(
      Stream.value(utf8.encode(jsonEncode(value))),
      200,
      headers: {'content-type': 'application/json'},
    );
  }
}

void main() {
  testWidgets('a new workspace projects the Ino navigation path', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final store = WorkspaceStore(persistence: MemoryPersistence());
    final api = FirstRunClient();
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: api,
    );
    await tester.pumpWidget(
      WorkspaceApp(
        store: store,
        programmingClient: client,
        connectedSources: const ['salesforce'],
      ),
    );
    await tester.pumpAndSettle();

    expect(
      store.remoteSnapshots[store.currentProject.id]?.path.last,
      '${store.currentProject.id}/apps/ino',
    );
    expect(store.firstRun, isNull);

    await tester.pumpWidget(const SizedBox());
    await api.events.close();
    store.dispose();
  });
}
