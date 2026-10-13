import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'settings_redesign_test.dart' show SettingsAppClient;
import 'workspace_remote_controller_test.dart' show MemoryPersistence;

class OpeningClient extends SettingsAppClient {
  int reopens = 0;
  int currentRevision = 10;
  bool windowOpen = false;

  /// The window the app commits for itself; the shell reveals the same id.
  String windowId = '';

  /// The surface neuron the app's window renders.
  String surfaceId = '';

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (request.url.path.endsWith('/show')) {
      currentRevision++;
      windowOpen = true;
      final response = await super.send(request);
      await response.stream.drain<void>();
      return http.StreamedResponse(
        Stream.value(
          utf8.encode(
            jsonEncode({...settingsSnapshot, 'revision': currentRevision}),
          ),
        ),
        200,
        headers: {'content-type': 'application/json'},
      );
    }
    if (request.url.path.endsWith('/reopen')) {
      reopens++;
      return http.StreamedResponse(
        Stream.value(utf8.encode('Stale revision')),
        409,
      );
    }
    if (request.url.pathSegments.length == 2 &&
        request.url.pathSegments.first == 'brains') {
      return http.StreamedResponse(
        Stream.value(
          utf8.encode(
            jsonEncode({
              'revision': currentRevision,
              'path': ['brain', if (windowOpen) windowId],
              'open': [if (windowOpen) windowId],
            }),
          ),
        ),
        200,
        headers: {'content-type': 'application/json'},
      );
    }
    return super.send(request);
  }
}

void main() {
  testWidgets(
    'app open reconciles committed window before stream invalidation',
    (tester) async {
      tester.view.physicalSize = const Size(1440, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final store = WorkspaceStore(persistence: MemoryPersistence());
      final appKey = '${store.currentProject.id}/apps/settings';
      store.currentProject.artifacts.add(
        WorkspaceArtifact(
          id: appKey,
          title: 'Settings',
          kind: 'surface',
          remoteManaged: true,
          data: {
            'surface': {'kind': 'surface', 'name': '$appKey/surface'},
          },
        ),
      );
      final api = OpeningClient()
        ..windowId = appKey
        ..surfaceId = '$appKey/surface';
      await tester.pumpWidget(
        WorkspaceApp(
          store: store,
          programmingClient: DigitalBrainUiClient(
            baseUri: Uri.parse('http://test'),
            httpClient: api,
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(store.remoteRevisions[store.currentProject.id], 10);
      await tester.tap(find.byTooltip('Settings'));
      await tester.pumpAndSettle();
      expect(api.openedSettings, 1);
      expect(api.reopens, 0, reason: 'The app already opened this window');
      expect(store.remoteRevisions[store.currentProject.id], 11);
      expect(
        store.currentProject.presentation.openArtifactIds,
        contains(api.windowId),
      );
      expect(find.text('Connections supplied by the app'), findsOneWidget);
      expect(find.textContaining('Window changed.'), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
      await api.events.close();
      store.dispose();
    },
  );
}
