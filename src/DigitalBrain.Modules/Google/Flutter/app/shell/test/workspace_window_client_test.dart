import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test('close and reopen carry the exact opaque window ID', () async {
    final requests = <http.Request>[];
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        requests.add(request);
        return http.Response('{"revision":4,"windows":[]}', 200);
      }),
    );
    for (final id in ['apps/chart/window', 'apps%2Fchart%2Fwindow']) {
      await client.closeWorkspaceWindow('brain', id, 2);
      await client.reopenWorkspaceWindow('brain', id, 3);
      expect(jsonDecode(requests[requests.length - 2].body)['windowId'], id);
      expect(jsonDecode(requests.last.body)['windowId'], id);
    }
  });
}
