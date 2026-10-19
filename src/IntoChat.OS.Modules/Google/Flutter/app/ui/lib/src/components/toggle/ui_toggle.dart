import 'package:flutter/material.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';

/// A labelled on/off switch backed by a server `toggle` neuron.
final class UiToggle extends StatelessWidget {
  const UiToggle({
    super.key,
    required this.label,
    required this.on,
    this.onChanged,
  });

  final String label;
  final bool on;
  final ValueChanged<bool>? onChanged;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return MergeSemantics(
      child: Row(
        children: [
          Expanded(child: Text(label, style: UiType.of(context).body)),
          const SizedBox(width: 12),
          Switch(
            value: on,
            onChanged: onChanged,
            activeThumbColor: c.onAccent,
            activeTrackColor: c.accent,
            inactiveThumbColor: c.surface,
            inactiveTrackColor: c.hairline,
            trackOutlineColor: WidgetStatePropertyAll(c.inputBorder),
          ),
        ],
      ),
    );
  }
}
