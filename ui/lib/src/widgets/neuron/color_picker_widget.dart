import 'dart:async';

import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import 'neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.ColorPicker.IColorPicker`.
class ColorPickerWidget extends StatelessWidget {
  const ColorPickerWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  static Color _parseHex(String hex) {
    final clean = hex.replaceFirst('#', '').trim();
    if (clean.length == 6) {
      return Color(int.tryParse('FF$clean', radix: 16) ?? 0xFF1F5EFF);
    }
    return const Color(0xFF1F5EFF);
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.color-picker',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = '${state['label'] ?? ''}';
        final hex = '${state['hex'] ?? '#1F5EFF'}';
        final isEnabled = state['isEnabled'] != false;
        final swatches = state['swatches'] is List
            ? [for (final s in state['swatches'] as List) '$s']
            : const <String>[];

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          spacing: 8,
          children: [
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                if (label.isNotEmpty) ...[
                  Text(
                    label,
                    style: TextStyle(
                      fontSize: 13.5,
                      fontWeight: FontWeight.w500,
                      color: c.textStrong,
                    ),
                  ),
                  const SizedBox(width: 10),
                ],
                Container(
                  width: 18,
                  height: 18,
                  decoration: BoxDecoration(
                    color: _parseHex(hex),
                    borderRadius: BorderRadius.circular(4),
                    border: Border.all(color: c.borderStrong),
                  ),
                ),
                const SizedBox(width: 6),
                Text(
                  hex,
                  style: TextStyle(
                    fontSize: 12.5,
                    fontWeight: FontWeight.w600,
                    color: c.textMuted,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
                ),
              ],
            ),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final swatch in swatches)
                  InkWell(
                    borderRadius: BorderRadius.circular(999),
                    onTap: isEnabled
                        ? () => unawaited(invoke('SetColor', [swatch]))
                        : null,
                    child: Container(
                      width: 28,
                      height: 28,
                      decoration: BoxDecoration(
                        color: _parseHex(swatch),
                        shape: BoxShape.circle,
                        border: Border.all(
                          color: swatch.toUpperCase() == hex.toUpperCase()
                              ? c.textStrong
                              : c.borderHairline,
                          width: swatch.toUpperCase() == hex.toUpperCase()
                              ? 2.5
                              : 1,
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ],
        );
      },
    );
  }
}
