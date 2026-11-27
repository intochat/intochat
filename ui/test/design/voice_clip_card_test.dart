import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets(
    'VoiceClipCard renders play/pause button, waveform bars, tabular duration, and expandable transcript',
    (tester) async {
      var playing = false;

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.intochat(Brightness.light),
          home: Scaffold(
            body: Center(
              child: SizedBox(
                width: 340,
                child: StatefulBuilder(
                  builder: (context, setState) => VoiceClipCard(
                    duration: const Duration(minutes: 1, seconds: 7),
                    progress: 0.5,
                    isPlaying: playing,
                    onTogglePlay: () => setState(() => playing = !playing),
                    transcript:
                        'Meet me by the studio entrance after the design sync.',
                  ),
                ),
              ),
            ),
          ),
        ),
      );

      final durationText = tester.widget<Text>(
        find.byKey(const ValueKey('voice_clip_duration')),
      );
      expect(durationText.data, '1:07');
      expect(durationText.style?.fontFamily, UiType.sans);
      expect(durationText.style?.fontFeatures, UiType.tabular);

      // Play/pause toggle fires callback and switches Lucide icon.
      expect(find.byIcon(LucideIcons.play), findsOneWidget);
      await tester.tap(find.byKey(const ValueKey('voice_clip_play_pause')));
      await tester.pumpAndSettle();
      expect(playing, isTrue);
      expect(find.byIcon(LucideIcons.pause), findsOneWidget);

      // Transcript starts collapsed (maxLines == 1, 13px textMuted) and expands on tap.
      final collapsedTranscript = tester.widget<Text>(
        find.text('Meet me by the studio entrance after the design sync.'),
      );
      expect(collapsedTranscript.maxLines, 1);
      expect(collapsedTranscript.style?.fontSize, 13);
      expect(
        collapsedTranscript.style?.color,
        UiColors.intochatLight.textMuted,
      );

      await tester.tap(
        find.byKey(const ValueKey('voice_clip_transcript_toggle')),
      );
      await tester.pumpAndSettle();

      final expandedTranscript = tester.widget<Text>(
        find.text('Meet me by the studio entrance after the design sync.'),
      );
      expect(expandedTranscript.maxLines, isNull);
    },
  );
}
