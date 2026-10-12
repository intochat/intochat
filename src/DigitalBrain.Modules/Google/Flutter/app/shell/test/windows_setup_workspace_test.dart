import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_settings.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import 'first_run_test.dart' show FirstRunClient;
import 'workspace_remote_controller_test.dart' show MemoryPersistence;

class WindowsSetupClient extends FirstRunClient {
  final discoveries = <String>[];
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final path = request.url.path;
    Object? value;
    if (path.endsWith('/windows/device')) {
      discoveries.add(request.url.pathSegments[1]);
      value = {'machineId': 'machine', 'machineName': 'HOST-PC'};
    } else if (path.endsWith('/windows/machine/roots')) {
      value = <String>[];
    } else if (path.endsWith('/connections')) {
      value = [
        {
          'connectorId': 'windows',
          'connectionId': 'machine',
          'status': 'Connected',
        },
      ];
    } else if (path.endsWith('/connectors')) {
      value = [
        {
          'id': 'windows',
          'displayName': 'Windows',
          'authMethods': ['device'],
        },
      ];
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
  for (final section in ['connections', 'general']) {
    testWidgets(
      'settings from $section preserve input on rebuild and clear it on brain switch',
      (tester) async {
        tester.view.physicalSize = const Size(1100, 1100);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        final store = WorkspaceStore(persistence: MemoryPersistence());
        final api = WindowsSetupClient();
        final client = DigitalBrainUiClient(
          baseUri: Uri.parse('http://test'),
          httpClient: api,
        );
        await tester.pumpWidget(
          WorkspaceApp(
            store: store,
            programmingClient: client,
            initialLocation: Uri.parse('/settings/$section'),
          ),
        );
        await tester.pumpAndSettle();
        if (section == 'general') {
          await tester.tap(find.text('Connections'));
          await tester.pumpAndSettle();
        }
        final input = find.byKey(const Key('windows_folder_path'));
        await tester.enterText(input, 'C:/unfinished');
        final routeBuilder = find
            .ancestor(
              of: find.byType(WorkspaceSettings),
              matching: find.byType(Builder),
            )
            .first;
        tester.element(routeBuilder).markNeedsBuild();
        await tester.pumpAndSettle();
        expect(
          tester.widget<TextField>(input).controller!.text,
          'C:/unfinished',
        );
        final original = store.currentProject.id;
        store.createProject('Another brain', id: 'another');
        await tester.pumpAndSettle();
        expect(store.currentProject.id, 'another');
        expect(tester.widget<TextField>(input).controller!.text, isEmpty);
        expect(api.discoveries, containsAll([original, 'another']));
        await tester.pumpWidget(const SizedBox());
        await api.events.close();
        store.dispose();
      },
    );
  }
}
