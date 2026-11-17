import 'package:flutter_test/flutter_test.dart';

import 'application_fixture.dart';

void main() {
  testWidgets(
    'the desktop shell and viewports watch their own neuron change signals',
    (tester) async {
      final f = await ApplicationFixture.start(tester);
      expect(
        f.link.watches.any(
          (w) => w.$2.any((s) => s.types.contains('FlutterViewChanged')),
        ),
        isTrue,
      );
      expect(
        f.link.watches.any(
          (w) => w.$2.any(
            (s) =>
                s.key == 'brain-1/apps/ino/surface' &&
                s.types.contains('FlutterViewChanged'),
          ),
        ),
        isTrue,
      );
      await f.dispose();
    },
  );
}
