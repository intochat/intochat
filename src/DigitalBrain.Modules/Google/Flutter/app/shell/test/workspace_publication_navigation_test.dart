import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/app_detail_screen.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import 'settings_redesign_test.dart' show SettingsAppClient;
import 'workspace_remote_controller_test.dart' show MemoryPersistence;

class PublicationClient extends SettingsAppClient {
  bool allowed = true;
  bool failOpen = false;
  final destinationCalls = <String>[];
  Map<String, dynamic>? submitted;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    Object? response;
    var status = 200;
    if (request.url.path == '/identity/brains') {
      response = allowed ? [{'id': 'circle-test', 'kind': 'circle'}] : [];
    } else if (request.url.path.startsWith('/brains/circle-test/')) {
      destinationCalls.add(request.url.path);
      if (request.url.path.endsWith('/request')) {
        submitted = jsonDecode((request as http.Request).body) as Map<String, dynamic>;
        response = {'requestId': 'test-request'};
      } else if (request.url.path.endsWith('/windows/open') && failOpen) {
        response = {'error': 'Unavailable'};
        status = 503;
      }
    }
    if (response == null) return super.send(request);
    return http.StreamedResponse(Stream.value(utf8.encode(jsonEncode(response))), status,
      headers: {'content-type': 'application/json'});
  }
}

void main() {
  for (final scenario in ['allowed', 'not a member', 'open fails', 'configuration']) {
    testWidgets('publication navigation: $scenario', (tester) async {
      tester.view.physicalSize = const Size(1440, 900);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final store = WorkspaceStore(persistence: MemoryPersistence());
      final original = store.currentProject;
      store.currentConversation.draft = 'Keep my draft';
      final api = PublicationClient()
        ..allowed = scenario != 'not a member'
        ..failOpen = scenario == 'open fails';
      await tester.pumpWidget(WorkspaceApp(store: store,
        programmingClient: DigitalBrainUiClient(baseUri: Uri.parse('http://test'), httpClient: api)));
      await tester.pumpAndSettle();
      await tester.tap(find.byTooltip('Settings'));
      await tester.pumpAndSettle();
      const exact = '{"name":"echo","version":"1.0.0","digest":"old-release"}';
      final view = tester.widgetList<NeuronView>(find.byType(NeuronView)).firstWhere((v) => v.name.endsWith('/settings/surface'));
      view.onActivate!({'kind': 'workspace', 'brainId': 'circle-test',
        'application': 'settings', 'operation': 'publication', 'input': exact,
        if (scenario == 'configuration') 'view': 'configuration'});
      await tester.pumpAndSettle();
      expect(original.conversations.first.draft, 'Keep my draft');
      if (scenario == 'configuration') {
        expect(find.byType(AppDetailScreen), findsOneWidget);
        expect(api.submitted, isNull);
        expect(api.destinationCalls.where((p) => p.endsWith('/windows/open')), isEmpty);
      } else if (scenario == 'allowed') {
        expect(store.currentProject.id, 'circle-test');
        expect(api.submitted, {'operation': 'publication', 'input': exact});
      } else {
        expect(store.currentProject.id, original.id);
        expect(api.submitted, isNull);
        if (scenario == 'not a member') expect(api.destinationCalls, isEmpty);
      }
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
      await api.events.close();
      store.dispose();
    });
  }
}
