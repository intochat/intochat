import 'package:flutter_test/flutter_test.dart';

import 'application_fixture.dart';

void main() {
  testWidgets(
    'the desktop shell and viewports watch their own neuron change signals',
    (tester) async {
      final f = await ApplicationFixture.start(tester);
      expect(
        f.link.watches.any(
          (w) => w.$2.any((s) => s.types.contains('ViewChanged')),
        ),
        isTrue,
      );
      expect(
        f.link.watches.any(
          (w) => w.$2.any((s) => s.types.contains('SurfaceChanged')),
        ),
        isTrue,
      );
      await f.dispose();
    },
  );
}
