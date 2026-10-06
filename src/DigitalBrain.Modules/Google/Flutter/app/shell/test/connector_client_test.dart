import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test(
    'catalog and connection requests use connector and named brain routes',
    () async {
      final requests = <http.Request>[];
      final client = DigitalBrainUiClient(
        baseUri: Uri.parse('http://test'),
        httpClient: MockClient((request) async {
          requests.add(request);
          return http.Response(jsonEncode([]), 200);
        }),
      );
      await client.connectorRequest('brain', '');
      await client.workspaceConnectionsRequest('brain', '');
      await client.connectorRequest(
        'brain',
        'gmail/start',
        body: {'connectionId': 'work mail', 'authMethodId': 'google-oauth'},
      );
      await client.connectorRequest(
        'brain',
        'salesforce/start',
        body: {'connectionId': 'crm'},
      );
      expect(requests.map((r) => r.url.path), [
        '/connectors',
        '/brains/brain/connections',
        '/brains/brain/connections/work%20mail/gmail/login',
        '/brains/brain/connections/services/salesforce/start',
      ]);
      expect(requests[2].method, 'POST');
      expect(jsonDecode(requests[2].body)['connectionId'], 'work mail');
    },
  );
}
