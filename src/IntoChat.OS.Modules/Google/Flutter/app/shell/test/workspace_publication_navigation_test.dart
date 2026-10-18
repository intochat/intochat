import 'package:flutter_test/flutter_test.dart';

import 'application_fixture.dart';

import 'package:digitalbrain_flutter_shell/space/neuron_space.dart';

void main() {
  testWidgets(
    'failed destination Show preserves the source controller and path',
    (tester) async {
      final f = await ApplicationFixture.start(tester);
      final source = f.space;
      final original = source.workspace!.path.toList();
      f.server.failOpen = true;
      tester.widget<NeuronSpace>(find.byType(NeuronSpace)).onActivate!({
        'brainId': 'circle-test',
        'application': 'notes',
      });
      await tester.pumpAndSettle();
      expect(f.space, same(source));
      expect(f.space.workspace!.path, original);
      expect(f.space.view.value.focused, '${f.brainId}/apps/ino');
      expect(find.textContaining('Could not navigate'), findsOneWidget);
      f.server.failOpen = false;
      await source.home();
      await tester.pumpAndSettle();
      expect(source.view.value.focused, f.brainId);
      await f.dispose();
    },
  );
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
