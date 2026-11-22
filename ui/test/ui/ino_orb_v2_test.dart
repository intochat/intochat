import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/src/widgets/ino/ino_orb_v2.dart';

void main() {
  testWidgets('InoOrbV2 renders all 8 PresenceStates and fires onTap', (
    tester,
  ) async {
    var tapped = 0;

    for (final state in PresenceState.values) {
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark(null),
          home: Scaffold(
            backgroundColor: Colors.transparent,
            body: Center(
              child: InoOrbV2(
                state: state,
                size: 80,
                audioLevel: state == PresenceState.listening ? 0.8 : 0.0,
                onTap: () => tapped++,
              ),
            ),
          ),
        ),
      );
      await tester.pump(const Duration(milliseconds: 50));
      expect(find.byType(InoOrbV2), findsOneWidget);
    }

    await tester.tap(find.byType(InoOrbV2));
    await tester.pump();
    expect(tapped, equals(1));
  });

  testWidgets(
    'InoFlyingOrbOverlay flies to new target Offset with status pill',
    (tester) async {
      var target = const Offset(40, 40);

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark(null),
          home: Scaffold(
            backgroundColor: Colors.transparent,
            body: StatefulBuilder(
              builder: (context, setState) => Stack(
                children: [
                  InoFlyingOrbOverlay(
                    position: target,
                    state: PresenceState.searching,
                    statusText: 'Inspecting Auth…',
                    onTap: () =>
                        setState(() => target = const Offset(300, 220)),
                  ),
                ],
              ),
            ),
          ),
        ),
      );
      await tester.pump();

      expect(find.text('Inspecting Auth…'), findsOneWidget);
      final initialPos = tester.getTopLeft(find.byType(InoOrbV2));

      await tester.tap(find.byType(InoOrbV2));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 600));

      final flownPos = tester.getTopLeft(find.byType(InoOrbV2));
      expect(flownPos.dx - initialPos.dx, closeTo(260, 20));
    },
  );
}
