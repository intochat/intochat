import 'dart:convert' show jsonEncode, jsonDecode;

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:digitalbrain_flutter_shell/space/neuron_space.dart';

import 'application_fixture.dart';

void main() {
  for (final target in ['brain-1', 'circle-test']) {
    testWidgets('root routes $target activation to its app-owned navigator', (
      tester,
    ) async {
      final server = ActivationServer(target);
      final fixture = await ApplicationFixture.start(tester, server: server);
      await tester.tap(find.text('Open target'));
      await tester.pumpAndSettle();
      final requests = server.requestsTo(
        '/brains/$target/apps/navigation/request',
      );
      expect(requests, hasLength(1));
      expect(requests.single.body['operation'], 'activate');
      expect(jsonDecode(requests.single.body['input'] as String), {
        'brainId': target,
        'application': 'target',
        'operation': 'profile',
        'input': 'person-1',
      });
      expect(
        server.requests.where(
          (r) =>
              r.path.endsWith('/show') ||
              r.path.endsWith('/apps/target/request'),
        ),
        isEmpty,
      );
      await fixture.dispose();
    });
  }

  testWidgets(
    'server-selected root mounts without legacy workspace navigation',
    (tester) async {
      final server = ApplicationServer()
        ..root = {'kind': 'surface', 'name': 'brain-1/apps/assistant/surface'};
      final fixture = await ApplicationFixture.start(tester, server: server);
      expect(find.byType(NeuronSpace), findsNothing);
      expect(find.text('Ask the assistant'), findsOneWidget);
      expect(server.requestsTo('/brains/brain-1/apps/connect'), hasLength(1));
      expect(
        server.requests.where(
          (r) => r.path.endsWith('/show') || r.path.endsWith('/request'),
        ),
        isEmpty,
      );
      await fixture.dispose();
    },
  );
}

class ActivationServer extends ApplicationServer {
  ActivationServer(this.targetBrain);
  final String targetBrain;
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (request.url.path.endsWith('/apps/connect')) {
      requests.add(RecordedAppRequest(request.url.path, body(request)));
      final brain = request.url.pathSegments[1];
      return json({
        'root': {'kind': 'button', 'name': '$brain/apps/source/go'},
        'activationApp': 'navigation',
      });
    }
    if (request.url.path.endsWith('/apps/node')) {
      return json({
        'label': 'Open target',
        'enabled': true,
        'activation': jsonEncode({
          'brainId': targetBrain,
          'application': 'target',
          'operation': 'profile',
          'input': 'person-1',
        }),
      });
    }
    return super.send(request);
  }
}
