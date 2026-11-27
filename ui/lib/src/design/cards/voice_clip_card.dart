import 'package:flutter/widgets.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../primitives/icon_button.dart';
import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';

/// Voice clip card (`DESIGN.md` compliant, Wave 2A/3B surface prep):
/// - Play/Pause [UiMixIconButton] (Lucide icons only)
/// - Waveform progress bars (`accent` played / `borderInput` unplayed)
/// - Tabular duration (`tnum: true`, Inter font only)
/// - Expandable 1-line `13px textMuted` auto-transcript
class VoiceClipCard extends StatefulWidget {
  const VoiceClipCard({
    super.key,
    required this.duration,
    this.progress = 0.0,
    this.isPlaying = false,
    this.onTogglePlay,
    this.samples = defaultSamples,
    this.transcript,
    this.initiallyExpanded = false,
  });

  /// Total clip duration (or current/total formatted as tabular `m:ss`).
  final Duration duration;

  /// Playback progress in `[0.0, 1.0]`.
  final double progress;

  final bool isPlaying;
  final VoidCallback? onTogglePlay;

  /// Normalized bar amplitudes in `[0.15, 1.0]`.
  final List<double> samples;

  /// Optional auto-transcript shown as an expandable 1-line `13px textMuted`
  /// summary below the waveform row.
  final String? transcript;

  final bool initiallyExpanded;

  static const List<double> defaultSamples = <double>[
    0.28,
    0.52,
    0.78,
    0.44,
    0.92,
    0.66,
    0.35,
    0.84,
    0.60,
    0.42,
    0.95,
    0.70,
    0.48,
    0.30,
    0.74,
    0.58,
    0.86,
    0.40,
    0.62,
    0.34,
  ];

  @override
  State<VoiceClipCard> createState() => _VoiceClipCardState();
}

class _VoiceClipCardState extends State<VoiceClipCard> {
  late bool _expanded = widget.initiallyExpanded;

  static String _formatDuration(Duration d) {
    final totalSeconds = d.inSeconds < 0 ? 0 : d.inSeconds;
    final minutes = totalSeconds ~/ 60;
    final seconds = totalSeconds % 60;
    return '$minutes:${seconds.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final clampedProgress = widget.progress.clamp(0.0, 1.0);
    final bars = widget.samples.isEmpty
        ? VoiceClipCard.defaultSamples
        : widget.samples;
    final playedCount = (clampedProgress * bars.length).round();
    final transcript = widget.transcript?.trim();
    final hasTranscript = transcript != null && transcript.isNotEmpty;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(tokens.card),
        border: Border.all(color: c.borderHairline),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            children: [
              UiMixIconButton(
                key: const ValueKey('voice_clip_play_pause'),
                icon: widget.isPlaying ? LucideIcons.pause : LucideIcons.play,
                iconSize: 15,
                size: 32,
                iconColor: c.accent,
                tooltip: widget.isPlaying
                    ? 'Pause voice clip'
                    : 'Play voice clip',
                onPressed: widget.onTogglePlay,
              ),
              const SizedBox(width: 10),
              Expanded(
                child: SizedBox(
                  key: const ValueKey('voice_clip_waveform'),
                  height: 26,
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.center,
                    children: [
                      for (var i = 0; i < bars.length; i++) ...[
                        if (i > 0) const SizedBox(width: 2.5),
                        Expanded(
                          child: Align(
                            alignment: Alignment.center,
                            child: Container(
                              height: 6 + (bars[i].clamp(0.15, 1.0) * 18),
                              decoration: BoxDecoration(
                                color: i < playedCount
                                    ? c.accent
                                    : c.borderInput,
                                borderRadius: BorderRadius.circular(2),
                              ),
                            ),
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
              ),
              const SizedBox(width: 10),
              Text(
                _formatDuration(widget.duration),
                key: const ValueKey('voice_clip_duration'),
                style: UiType.textStyle(
                  12.5,
                  weight: FontWeight.w500,
                  color: c.textMuted,
                  tnum: true,
                ),
              ),
            ],
          ),
          if (hasTranscript) ...[
            const SizedBox(height: 8),
            GestureDetector(
              key: const ValueKey('voice_clip_transcript_toggle'),
              behavior: HitTestBehavior.opaque,
              onTap: () => setState(() => _expanded = !_expanded),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Text(
                      transcript,
                      maxLines: _expanded ? null : 1,
                      overflow: _expanded
                          ? TextOverflow.visible
                          : TextOverflow.ellipsis,
                      style: UiType.textStyle(
                        13,
                        weight: FontWeight.w400,
                        color: c.textMuted,
                        height: 1.45,
                      ),
                    ),
                  ),
                  const SizedBox(width: 6),
                  Padding(
                    padding: const EdgeInsets.only(top: 2),
                    child: Icon(
                      _expanded
                          ? LucideIcons.chevronUp
                          : LucideIcons.chevronDown,
                      size: 14,
                      color: c.textMuted,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}
