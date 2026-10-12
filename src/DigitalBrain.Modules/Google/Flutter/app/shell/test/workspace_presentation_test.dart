import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'workspace_data_window_test.dart' show WorkspaceClient;
import 'workspace_remote_controller_test.dart' show MemoryPersistence;

class PresentationClient extends WorkspaceClient {
  final mutations = <String>[];
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (request.method != 'GET') mutations.add(request.url.path);
    Object? value;
    if (request.url.pathSegments.length == 2 &&
        request.url.pathSegments.first == 'brains') {
      value = {
        'revision': 8,
        'windows': [],
        'focusedWindow': null,
        'home': {'kind': 'text', 'name': 'brain/home'},
        'companion': {'kind': 'text', 'name': 'brain/companion'},
      };
    } else if (request.url.queryParameters['name'] == 'brain/home') {
      value = {'markdown': 'Home supplied by workspace'};
    } else if (request.url.queryParameters['name'] == 'brain/companion') {
      value = {'markdown': 'Companion supplied by workspace'};
    }
    if (value == null) return super.send(request);
    return http.StreamedResponse(
      Stream.value(utf8.encode(jsonEncode(value))),
      200,
      headers: {'content-type': 'application/json'},
    );
  }
}

class ConfiguringClient extends PresentationClient {
  int configurations = 0, reads = 0;
  Map<String, dynamic>? configured;
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    Object? value;
    if (request.url.path.endsWith('/presentation')) {
      configurations++;
      if (configurations == 1) {
        return http.StreamedResponse(
          Stream.value(utf8.encode('conflict')),
          409,
        );
      }
      configured =
          jsonDecode((request as http.Request).body) as Map<String, dynamic>;
      value = {'revision': 10, 'windows': [], ...configured!};
    } else if (request.url.path.endsWith('/apps/ino') ||
        request.url.path.endsWith('/apps/assistant')) {
      value = {
        'manifest': {
          'title': 'App',
          'entry': {'kind': 'text', 'name': 'entry'},
        },
      };
    } else if (request.url.pathSegments.length == 2 &&
        request.url.pathSegments.first == 'brains') {
      reads++;
      value = {
        'revision': reads == 1 ? 8 : 9,
        'windows': [],
        if (reads > 1) 'home': {'kind': 'text', 'name': 'existing/home'},
      };
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
  testWidgets(
    'presentation conflict retries current revision preserving configured Home',
    (tester) async {
      final store = WorkspaceStore(persistence: MemoryPersistence());
      final api = ConfiguringClient();
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
      expect(api.configurations, 2);
      expect(api.configured!['expectedRevision'], 9);
      expect(api.configured!['home'], {
        'kind': 'text',
        'name': 'existing/home',
      });
      await tester.pumpWidget(const SizedBox());
      await api.events.close();
      store.dispose();
    },
  );

  testWidgets('mount reads declared Home and companion without starting apps', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    final store = WorkspaceStore(persistence: MemoryPersistence());
    final api = PresentationClient();
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
    expect(find.text('Home supplied by workspace'), findsOneWidget);
    expect(find.text('Companion supplied by workspace'), findsOneWidget);
    expect(api.mutations.where((path) => path.contains('/apps/')), isEmpty);
    expect(find.byTooltip('Connections'), findsOneWidget);
    expect(find.text('Apps and brain'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    await api.events.close();
    store.dispose();
  });
}
