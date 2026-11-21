import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../primitives/button.dart';
import '../../design/tokens.dart';
import '../../theme/wire_node_values.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Dialog.IDialog`.
///
/// Renders nothing when `isOpen == false`, and a crisp WinUI 3-style modal card
/// with optional text prompt and confirm/cancel actions when `isOpen == true`.
class DialogWidget extends StatefulWidget {
  const DialogWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<DialogWidget> createState() => _DialogWidgetState();
}

class _DialogWidgetState extends State<DialogWidget> {
  final _inputController = TextEditingController();
  int _lastVersion = -1;

  @override
  void dispose() {
    _inputController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.dialog',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        if (state['isOpen'] != true) {
          return const SizedBox.shrink();
        }
        final c = IcTheme.of(context);
        final version = (state['version'] as num?)?.toInt() ?? 0;
        if (version != _lastVersion) {
          _lastVersion = version;
          _inputController.text = '${state['inputValue'] ?? ''}';
        }

        final title = '${state['title'] ?? 'Confirm'}';
        final message = '${state['message'] ?? ''}';
        final confirmLabel = '${state['confirmLabel'] ?? 'Confirm'}';
        final cancelLabel = '${state['cancelLabel'] ?? 'Cancel'}';
        final tone = '${state['tone'] ?? 'accent'}';
        final inputLabel = state['inputLabel'] as String?;
        final inputPlaceholder = state['inputPlaceholder'] as String?;

        return Container(
          padding: const EdgeInsets.all(18),
          decoration: BoxDecoration(
            color: c.surfaceElevated,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(
              color: tone == 'danger'
                  ? c.error.withValues(alpha: 0.5)
                  : c.accent.withValues(alpha: 0.45),
              width: 1.5,
            ),
            boxShadow: [
              BoxShadow(
                color: Colors.black.withValues(alpha: 0.14),
                blurRadius: 20,
                offset: const Offset(0, 8),
              ),
            ],
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            spacing: 12,
            children: [
              Row(
                children: [
                  Icon(
                    tone == 'danger'
                        ? LucideIcons.triangleAlert
                        : LucideIcons.info,
                    size: 18,
                    color: tone == 'danger' ? c.error : c.accent,
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      title,
                      style: TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                ],
              ),
              if (message.isNotEmpty)
                Text(
                  message,
                  style: TextStyle(
                    fontSize: 13.5,
                    height: 1.45,
                    color: c.textBody,
                  ),
                ),
              if (inputLabel != null && inputLabel.isNotEmpty)
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      inputLabel,
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                    const SizedBox(height: 6),
                    TextField(
                      controller: _inputController,
                      autofocus: true,
                      style: TextStyle(fontSize: 13.5, color: c.textStrong),
                      decoration: InputDecoration(
                        hintText: inputPlaceholder ?? '',
                        hintStyle: TextStyle(fontSize: 13, color: c.textMuted),
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
                      onSubmitted: (val) =>
                          unawaited(invoke('Confirm', [val.trim()])),
                    ),
                  ],
                ),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                spacing: 8,
                children: [
                  UiKitButton(
                    label: cancelLabel,
                    compact: true,
                    style: parseButtonStyle('ghost', 'neutral'),
                    onPressed: () => unawaited(invoke('Cancel')),
                  ),
                  UiKitButton(
                    label: confirmLabel,
                    compact: true,
                    style: parseButtonStyle(
                      tone == 'danger' ? 'danger' : 'primary',
                      tone,
                    ),
                    onPressed: () => unawaited(
                      invoke('Confirm', [
                        if (inputLabel != null && inputLabel.isNotEmpty)
                          _inputController.text.trim()
                        else
                          null,
                      ]),
                    ),
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
