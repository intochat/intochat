import 'dart:convert';

import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test(
    'catalog and connection requests use connector and named brain routes',
    () async {
      final requests = <http.Request>[];
      final client = IntoChatOSUiClient(
        baseUri: Uri.parse('http://test'),
        httpClient: MockClient((request) async {
          requests.add(request);
          return http.Response(jsonEncode([]), 200);
        }),
      );
      await client.connectorRequest('brain', '');
      await client.listWorkspaceConnections('brain');
      await client.connectWorkspaceSecret(
        'brain',
        'one',
        connectorId: 'gmail',
        authMethodId: 'token',
        value: 'secret',
      );
      await client.probeWorkspaceConnection('brain', 'one');
      await client.disconnectWorkspaceConnection('brain', 'one');
      await client.startWorkspaceConnectionLogin(
        'brain',
        'one',
        connectorId: 'gmail',
        authMethodId: 'google-oauth',
      );
      expect(requests.map((r) => r.url.path), [
        '/connectors',
        '/brains/brain/connections',
        '/brains/brain/connections/one/connect',
        '/brains/brain/connections/one/probe',
        '/brains/brain/connections/one/disconnect',
        '/brains/brain/connections/one/login',
      ]);
      expect(requests.skip(2).map((r) => r.method), everyElement('POST'));
      expect(jsonDecode(requests[5].body)['authMethodId'], 'google-oauth');
    },
  );
}
