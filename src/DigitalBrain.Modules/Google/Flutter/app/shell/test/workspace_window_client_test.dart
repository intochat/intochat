import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test(
    'navigation commands carry the exact neuron key and operation ID',
    () async {
      final requests = <http.Request>[];
      final client = DigitalBrainUiClient(
        baseUri: Uri.parse('http://test'),
        httpClient: MockClient((request) async {
          requests.add(request);
          return http.Response(
            '{"revision":4,"path":["brain"],"open":[]}',
            200,
          );
        }),
      );
      for (final id in ['apps/chart/window', 'apps%2Fchart%2Fwindow']) {
        await client.closeNeuron('brain', id, 'close', 2);
        await client.showNeuron('brain', id, 'show', 3);
        expect(jsonDecode(requests[requests.length - 2].body)['app'], id);
        expect(jsonDecode(requests.last.body)['app'], id);
        expect(jsonDecode(requests.last.body)['operationId'], 'show');
        expect(jsonDecode(requests.last.body)['expectedRevision'], 3);
      }
      await client.backNeuron('brain', 'back', 4);
      await client.homeNeuron('brain', 'home', 5);
      expect(requests.map((request) => request.url.path).toList().sublist(4), [
        '/brains/brain/back',
        '/brains/brain/home',
      ]);
      expect(jsonDecode(requests.last.body).containsKey('app'), isFalse);
      client.close();
    },
  );
}
