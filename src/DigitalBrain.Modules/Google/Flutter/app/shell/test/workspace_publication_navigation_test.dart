import 'package:flutter_test/flutter_test.dart';

import 'application_fixture.dart';

import 'package:digitalbrain_flutter_shell/space/neuron_space.dart';

void main() {
  for (final allowed in [false, true]) {
    testWidgets(
      'publication navigation checks destination membership $allowed',
      (tester) async {
        final f = await ApplicationFixture.start(
          tester,
          server: ApplicationServer()..allowed = allowed,
        );
        tester.widget<NeuronSpace>(find.byType(NeuronSpace)).onActivate!({
          'kind': 'workspace',
          'brainId': 'circle-test',
          'application': 'notes',
          'operation': 'open',
          'input': 'shared',
        });
        await tester.pumpAndSettle();
        if (allowed) {
          expect(f.space.brain, 'circle-test');
          expect(f.space.view.value.focused, 'circle-test/apps/notes');
          expect(
            f.server
                .requestsTo('/brains/circle-test/apps/notes/request')
                .single
                .body,
            {'operation': 'open', 'input': 'shared'},
          );
        } else {
          expect(f.space.brain, f.brainId);
          expect(
            f.server.requests.any(
              (r) => r.path.startsWith('/brains/circle-test/'),
            ),
            isFalse,
          );
          expect(find.textContaining('not a member'), findsOneWidget);
        }
        await f.dispose();
      },
    );
  }
}
