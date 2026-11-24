import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../../primitives/primitives.dart';
import 'neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.AutoSuggestBox.IAutoSuggestBox`.
class AutoSuggestBoxWidget extends StatefulWidget {
  const AutoSuggestBoxWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<AutoSuggestBoxWidget> createState() => _AutoSuggestBoxWidgetState();
}

class _AutoSuggestBoxWidgetState extends State<AutoSuggestBoxWidget> {
  final _controller = TextEditingController();
  Timer? _debounce;

  @override
  void dispose() {
    _debounce?.cancel();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.auto-suggest',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final placeholder = '${state['placeholder'] ?? 'Search…'}';
        final submitted = state['submittedText'] as String?;
        final suggestions = state['suggestions'] is List
            ? [for (final s in state['suggestions'] as List) '$s']
            : const <String>[];

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          spacing: 6,
          children: [
            TextField(
              controller: _controller,
              style: TextStyle(fontSize: 13.5, color: c.textStrong),
              decoration: InputDecoration(
                hintText: placeholder,
                hintStyle: TextStyle(fontSize: 13, color: c.textMuted),
                prefixIcon: Icon(
                  LucideIcons.search,
                  size: 15,
                  color: c.textMuted,
                ),
                isDense: true,
                contentPadding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 10,
                ),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(IcRadius.sm),
                  borderSide: BorderSide(color: c.borderInput),
                ),
              ),
              onChanged: (q) {
                _debounce?.cancel();
                _debounce = Timer(
                  const Duration(milliseconds: 180),
                  () => unawaited(invoke('SetQuery', [q])),
                );
              },
              onSubmitted: (text) => unawaited(invoke('Submit', [text])),
            ),
            if (suggestions.isNotEmpty)
              Wrap(
                spacing: 6,
                runSpacing: 4,
                children: [
                  for (final s in suggestions)
                    UiMixChip(
                      label: s,
                      selected: s == submitted,
                      onSelected: (_) {
                        _controller.text = s;
                        unawaited(invoke('Submit', [s]));
                      },
                    ),
                ],
              ),
            if (submitted != null && submitted.isNotEmpty)
              Text(
                'Selected: $submitted',
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w600,
                  color: c.accent,
                ),
              ),
          ],
        );
      },
    );
  }
}
