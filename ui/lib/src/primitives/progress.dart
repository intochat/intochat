import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';

/// Backward-compatible alias for [UiMixProgress].
typedef UiProgress = UiMixProgress;

/// Option B `mix`-styled linear or circular progress indicator; [value] is
/// clamped to 0..1 and ignored when [determinate] is false.
final class UiMixProgress extends StatelessWidget {
  const UiMixProgress({
    super.key,
    required this.determinate,
    required this.value,
    required this.label,
    this.error = false,
    this.ring = false,
  });

  final bool determinate;
  final double value;
  final String label;

  /// Draws it in the error tone: the work stopped.
  final bool error;

  /// Draws a 20px ring beside the label instead of a bar.
  final bool ring;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final fraction = value.isNaN ? 0.0 : value.clamp(0.0, 1.0);
    final color = error ? c.error : c.accent;

    if (ring) {
      return Row(
        mainAxisSize: MainAxisSize.min,
        spacing: 8,
        children: [
          SizedBox.square(
            dimension: 20,
            child: CircularProgressIndicator(
              value: determinate ? fraction : null,
              strokeWidth: 3,
              color: color,
              backgroundColor: c.recessed,
              semanticsLabel: label.isEmpty ? null : label,
            ),
          ),
          if (label.isNotEmpty)
            Flexible(child: Text(label, style: UiType.of(context).bodyMuted)),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        if (label.isNotEmpty) ...[
          Text(label, style: UiType.of(context).bodyMuted),
          const SizedBox(height: 8),
        ],
        Box(
          style: BoxStyler()
              .height(8)
              .borderRadius(BorderRadiusGeometryMix.circular(999)),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: LinearProgressIndicator(
              value: determinate ? fraction : null,
              minHeight: 8,
              color: color,
              backgroundColor: c.recessed,
              semanticsLabel: label.isEmpty ? null : label,
            ),
          ),
        ),
      ],
    );
  }
}
