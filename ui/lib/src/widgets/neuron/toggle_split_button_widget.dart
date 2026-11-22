import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../../theme/ui_icon.dart';
import 'neuron_widget.dart';

/// Reactive WinUI 3 ToggleSplitButton presenter for `IntoChat.OS.Modules.UI.ToggleSplitButton.IToggleSplitButton`.
class ToggleSplitButtonNeuronWidget extends StatelessWidget {
  const ToggleSplitButtonNeuronWidget({
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
      kind: 'ui.toggle-split-button',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final label = (state['label'] as String?) ?? 'Toggle';
        final iconSlug = state['icon'] as String?;
        final isChecked = state['isChecked'] == true;
        final isEnabled = state['isEnabled'] != false;
        final rawOptions = (state['options'] as List?) ?? const [];
        final options = <Map<String, dynamic>>[
          for (final item in rawOptions)
            if (item is Map) Map<String, dynamic>.from(item),
        ];

        final bg = isChecked ? c.accent : c.surfaceElevated;
        final fg = isChecked ? c.onAccent : c.textStrong;
        final dividerColor = isChecked
            ? c.onAccent.withValues(alpha: 0.3)
            : c.borderHairline;

        return Opacity(
          opacity: isEnabled ? 1.0 : 0.55,
          child: Container(
            decoration: BoxDecoration(
              color: bg,
              borderRadius: BorderRadius.circular(IcRadius.sm),
              border: Border.all(
                color: isChecked ? c.accent : c.borderHairline,
              ),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Semantics(
                  identifier: 'toggle-split-primary:$neuronKey',
                  button: true,
                  toggled: isChecked,
                  child: InkWell(
                    borderRadius: const BorderRadius.horizontal(
                      left: Radius.circular(IcRadius.sm),
                    ),
                    onTap: isEnabled
                        ? () => unawaited(invoke('Toggle', const []))
                        : null,
                    child: Padding(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 12,
                        vertical: 8,
                      ),
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Icon(
                            iconSlug != null && uiIcon(iconSlug) != null
                                ? uiIcon(iconSlug)!
                                : (isChecked
                                      ? LucideIcons.check
                                      : LucideIcons.circleDot),
                            size: 14,
                            color: fg,
                          ),
                          const SizedBox(width: 6),
                          Text(
                            label,
                            style: TextStyle(
                              fontSize: 12.5,
                              fontWeight: FontWeight.w600,
                              color: fg,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
                Container(width: 1, height: 22, color: dividerColor),
                PopupMenuButton<String>(
                  enabled: isEnabled && options.isNotEmpty,
                  tooltip: 'Select option',
                  icon: Icon(LucideIcons.chevronDown, size: 15, color: fg),
                  onSelected: (id) => unawaited(invoke('SelectOption', [id])),
                  itemBuilder: (context) => [
                    for (final opt in options)
                      PopupMenuItem<String>(
                        value: '${opt['id'] ?? ''}',
                        child: Text('${opt['label'] ?? opt['id'] ?? ''}'),
                      ),
                  ],
                ),
              ],
            ),
          ),
        );
      },
    );
  }
}
