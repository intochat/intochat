@Tags(['golden'])
library;

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

final class _SpherePainter extends CustomPainter {
  _SpherePainter(this.sphere);
  final GlassSphere sphere;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.translate(
      (size.width - sphere.size.x) / 2,
      (size.height - sphere.size.y) / 2,
    );
    sphere.render(canvas);
  }

  @override
  bool shouldRepaint(_SpherePainter oldDelegate) => true;
}

void main() {
  for (final state in PresenceState.values) {
    testWidgets('glass sphere ${state.name}', (tester) async {
      final sphere = GlassSphere(state: state, neuronKind: 'search', radius: 40)
        ..animations = false;
      await tester.pumpWidget(
        MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: Directionality(
            textDirection: TextDirection.ltr,
            child: Center(
              child: RepaintBoundary(
                child: SizedBox.square(
                  dimension: 120,
                  child: CustomPaint(painter: _SpherePainter(sphere)),
                ),
              ),
            ),
          ),
        ),
      );
      await expectLater(
        find.byType(RepaintBoundary).first,
        matchesGoldenFile('goldens/glass_sphere_${state.name}.png'),
      );
    });
  }
}
