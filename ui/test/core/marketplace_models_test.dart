import 'dart:convert';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  test('an installed app summary decodes its metadata and update offer', () {
    final app = InstalledAppSummary.fromJson({
      'name': 'customer-researcher',
      'title': 'Customer Researcher',
      'description': 'Research companies',
      'status': 'Live',
      'version': '1.2.0',
      'operations': ['open'],
      'icon': 'icon.svg',
      'publishedVersion': null,
      'edited': false,
      'updateAvailable': true,
    });
    expect(app.id, 'customer-researcher');
    expect(app.icon, 'icon.svg');
    expect(app.edited, isFalse);
    expect(app.updateAvailable, isTrue);
  });

  test('an app of the brain\'s own defaults optional metadata cleanly', () {
    final app = InstalledAppSummary.fromJson({
      'name': 'shouter',
      'status': 'Live',
      'version': '0.2.0',
      'operations': <String>[],
    });
    expect(app.icon, isNull);
    expect(app.edited, isFalse);
    expect(app.updateAvailable, isFalse);
  });

  test(
    'showing a neuron uses workspace state without app preparation',
    () async {
      final requests = <String>[];
      Map<String, dynamic>? opened;
      final client = IntoChatOSUiClient(
        baseUri: Uri.parse('http://test'),
        httpClient: MockClient((request) async {
          requests.add('${request.method} ${request.url.path}');
          if (request.url.path.endsWith('/show')) {
            opened = jsonDecode(request.body) as Map<String, dynamic>;
            return http.Response(
              jsonEncode({
                'revision': 4,
                'path': ['b1', 'b1/apps/ino'],
                'open': ['b1/apps/ino'],
              }),
              200,
            );
          }
          return http.Response(
            jsonEncode({
              'revision': 3,
              'path': ['b1'],
              'open': [],
            }),
            200,
          );
        }),
      );
      final before = await client.readWorkspace('b1');
      final state = await client.showNeuron(
        'b1',
        'b1/apps/ino',
        'open',
        before.revision,
      );
      expect(opened!['app'], 'b1/apps/ino');
      expect(opened!['expectedRevision'], 3);
      expect(state.path.last, 'b1/apps/ino');
      expect(requests.where((r) => r.endsWith('/request')), isEmpty);
      client.close();
    },
  );
}
