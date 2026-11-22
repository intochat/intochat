import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../../primitives/primitives.dart';
import 'neuron_widget.dart';

class MediaPlayerNeuronWidget extends StatelessWidget {
  const MediaPlayerNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  String _fmt(double seconds) {
    final total = seconds.round().clamp(0, 35999);
    final m = total ~/ 60;
    final s = total % 60;
    return '$m:${s.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.media-player',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = (state['title'] as String?) ?? 'Track';
        final artist = (state['artist'] as String?) ?? '';
        final playing = state['playing'] == true;
        final pos = (state['positionSeconds'] as num?)?.toDouble() ?? 0.0;
        final dur = ((state['durationSeconds'] as num?)?.toDouble() ?? 180.0)
            .clamp(1.0, 86400.0);
        final vol = ((state['volume'] as num?)?.toDouble() ?? 0.8).clamp(
          0.0,
          1.0,
        );
        final muted = state['muted'] == true;

        return Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  UiMixIconButton(
                    tooltip: playing ? 'Pause' : 'Play',
                    icon: playing ? Icons.pause : Icons.play_arrow,
                    iconSize: 18,
                    iconColor: c.accent,
                    onPressed: () =>
                        invoke(playing ? 'Pause' : 'Play', const []),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          title,
                          style: TextStyle(
                            fontSize: 13,
                            fontWeight: FontWeight.w600,
                            color: c.textStrong,
                          ),
                        ),
                        if (artist.isNotEmpty)
                          Text(
                            artist,
                            style: TextStyle(fontSize: 11, color: c.textMuted),
                          ),
                      ],
                    ),
                  ),
                  UiMixIconButton(
                    tooltip: muted ? 'Unmute' : 'Mute',
                    icon: muted ? Icons.volume_off : Icons.volume_up,
                    iconSize: 16,
                    onPressed: () => invoke('SetVolume', [vol, !muted]),
                  ),
                ],
              ),
              UiMixSlider(
                value: pos.clamp(0.0, dur),
                min: 0,
                max: dur,
                step: 1.0,
                onChanged: (v) => invoke('Seek', [v]),
              ),
              Row(
                mainAxisAlignment: MainAxisAlignment.spaceBetween,
                children: [
                  Text(
                    _fmt(pos),
                    style: TextStyle(fontSize: 11, color: c.textMuted),
                  ),
                  Text(
                    _fmt(dur),
                    style: TextStyle(fontSize: 11, color: c.textMuted),
                  ),
                ],
              ),
            ],
          ),
        );
      },
    );
  }
}
