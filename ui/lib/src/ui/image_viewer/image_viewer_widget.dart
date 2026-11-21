import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../mix/ui_mix_controls.dart';
import '../neuron_widget.dart';

class ImageViewerNeuronWidget extends StatelessWidget {
  const ImageViewerNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.image-viewer',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = (state['title'] as String?) ?? 'Image';
        final sourceUrl = (state['sourceUrl'] as String?) ?? '';
        final caption = state['caption'] as String?;
        final zoom = ((state['zoom'] as num?)?.toDouble() ?? 1.0).clamp(
          0.25,
          4.0,
        );
        final rotation = (state['rotationDegrees'] as num?)?.toInt() ?? 0;

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
                  Expanded(
                    child: Text(
                      title,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                  Text(
                    '${(zoom * 100).round()}% · $rotation°',
                    style: TextStyle(fontSize: 11, color: c.textMuted),
                  ),
                  UiMixIconButton(
                    tooltip: 'Zoom out',
                    icon: Icons.zoom_out,
                    iconSize: 16,
                    onPressed: () =>
                        invoke('SetZoom', [(zoom - 0.25).clamp(0.25, 4.0)]),
                  ),
                  UiMixIconButton(
                    tooltip: 'Zoom in',
                    icon: Icons.zoom_in,
                    iconSize: 16,
                    onPressed: () =>
                        invoke('SetZoom', [(zoom + 0.25).clamp(0.25, 4.0)]),
                  ),
                  UiMixIconButton(
                    tooltip: 'Rotate right',
                    icon: Icons.rotate_right,
                    iconSize: 16,
                    onPressed: () => invoke('Rotate', [90]),
                  ),
                  UiMixIconButton(
                    tooltip: 'Reset view',
                    icon: Icons.refresh,
                    iconSize: 16,
                    onPressed: () => invoke('ResetView', const []),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              ClipRRect(
                borderRadius: BorderRadius.circular(IcRadius.sm),
                child: Container(
                  height: 160,
                  width: double.infinity,
                  color: c.surfaceSunk,
                  alignment: Alignment.center,
                  child: Transform.rotate(
                    angle: rotation * math.pi / 180,
                    child: Transform.scale(
                      scale: zoom,
                      child: sourceUrl.isNotEmpty
                          ? Image.network(
                              sourceUrl,
                              fit: BoxFit.contain,
                              errorBuilder: (_, _, _) => Icon(
                                Icons.image_outlined,
                                size: 42,
                                color: c.textMuted,
                              ),
                            )
                          : Icon(
                              Icons.image_outlined,
                              size: 42,
                              color: c.textMuted,
                            ),
                    ),
                  ),
                ),
              ),
              if (caption != null && caption.isNotEmpty) ...[
                const SizedBox(height: 6),
                Text(
                  caption,
                  style: TextStyle(fontSize: 11, color: c.textMuted),
                ),
              ],
            ],
          ),
        );
      },
    );
  }
}
