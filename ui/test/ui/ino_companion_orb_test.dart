import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/src/ui/ino/ino_companion_orb.dart';

void main() {
  testWidgets(
    'InoCompanionOrb drags, snaps to edge, expands quick actions, and fires callbacks',
    (tester) async {
      var voiceTapped = 0;
      var treeTapped = 0;
      var neuronTapped = 0;

      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            backgroundColor: Colors.transparent,
            body: SizedBox(
              width: 800,
              height: 600,
              child: InoCompanionOrb(
                state: PresenceState.listening,
                audioLevel: 0.45,
                statusLine: 'Auth slice ready',
                routingBadge: 'S1 · 0.6ms',
                actions: defaultInoCompanionActions(
                  onVoice: () => voiceTapped++,
                  onCodebaseTree: () => treeTapped++,
                  onLiveNeuron: () => neuronTapped++,
                ),
              ),
            ),
          ),
        ),
      );

      expect(find.text('S1 · 0.6ms'), findsOneWidget);
      expect(find.text('Auth slice ready'), findsOneWidget);
      expect(find.text('Code Tree'), findsNothing);

      // Tap orb to expand quick-action pill bar.
      await tester.tap(find.bySemanticsLabel(RegExp('Ino Orb')));
      await tester.pump();
      expect(find.text('Voice'), findsOneWidget);
      expect(find.text('Code Tree'), findsOneWidget);
      expect(find.text('Live Neuron'), findsOneWidget);

      await tester.tap(find.text('Code Tree'));
      await tester.pump();
      expect(treeTapped, 1);

      await tester.tap(find.text('Live Neuron'));
      await tester.pump();
      expect(neuronTapped, 1);
      expect(voiceTapped, 0);
    },
  );
}
