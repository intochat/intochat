import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../ui/mix/ui_mix_button.dart';
import '../../design/tokens.dart';
import '../../theme/wire_node_values.dart';
import '../neuron_widget.dart';

/// Reactive presenter for `IntoChat.OS.Modules.UI.Form.IForm`.
///
/// Non-secret fields edit locally and submit as a typed map to `IForm.Submit`.
/// Secret fields (`kind == 'secret'`) never send plaintext to `IForm`; they mask
/// input and allow sealing an opaque `SecretRef` handle via `IForm.SetSecretRef`.
class FormWidget extends StatefulWidget {
  const FormWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<FormWidget> createState() => _FormWidgetState();
}

class _FormWidgetState extends State<FormWidget> {
  final _controllers = <String, TextEditingController>{};

  TextEditingController _controllerFor(String id, String initial) {
    return _controllers.putIfAbsent(
      id,
      () => TextEditingController(text: initial),
    );
  }

  @override
  void dispose() {
    for (final c in _controllers.values) {
      c.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.form',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = '${state['title'] ?? ''}';
        final submitLabel = '${state['submitLabel'] ?? 'Save'}';
        final isEnabled = state['isEnabled'] != false;
        final error = state['error'] as String?;
        final rawFields = state['fields'] is List
            ? state['fields'] as List
            : const <dynamic>[];
        final values = state['values'] is Map
            ? Map<String, dynamic>.from(state['values'] as Map)
            : const <String, dynamic>{};
        final secretRefs = state['secretRefs'] is Map
            ? Map<String, dynamic>.from(state['secretRefs'] as Map)
            : const <String, dynamic>{};

        final fieldWidgets = <Widget>[];
        final nonSecretIds = <String>[];

        for (final raw in rawFields) {
          if (raw is! Map) continue;
          final id = '${raw['id'] ?? ''}';
          final label = '${raw['label'] ?? id}';
          final kind = '${raw['kind'] ?? 'text'}'.toLowerCase();
          final placeholder = '${raw['placeholder'] ?? ''}';
          final required = raw['required'] == true;
          final isSecret = kind == 'secret';

          if (!isSecret) {
            nonSecretIds.add(id);
          }

          final controller = _controllerFor(
            id,
            isSecret ? '' : '${values[id] ?? ''}',
          );
          final hasSealed = isSecret && secretRefs.containsKey(id);

          fieldWidgets.add(
            Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Row(
                  children: [
                    Text(
                      required ? '$label *' : label,
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                    if (hasSealed) ...[
                      const SizedBox(width: 8),
                      Icon(LucideIcons.shieldCheck, size: 13, color: c.done),
                      const SizedBox(width: 4),
                      Text(
                        'Sealed in vault',
                        style: TextStyle(fontSize: 11.5, color: c.done),
                      ),
                    ],
                  ],
                ),
                const SizedBox(height: 6),
                TextField(
                  controller: controller,
                  enabled: isEnabled,
                  obscureText: isSecret,
                  maxLines: kind == 'multiline' ? 3 : 1,
                  keyboardType: kind == 'number'
                      ? TextInputType.number
                      : TextInputType.text,
                  style: TextStyle(fontSize: 13.5, color: c.textStrong),
                  decoration: InputDecoration(
                    hintText: hasSealed
                        ? '•••••••• (sealed — enter new value to rotate)'
                        : placeholder,
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
                  onSubmitted: isSecret
                      ? (_) {
                          final rawSecret = controller.text.trim();
                          if (rawSecret.isEmpty) return;
                          controller.clear();
                          unawaited(
                            invoke('SetSecretRef', [
                              id,
                              {'vaultKey': 'vault://$id', 'mask': '••••••••'},
                            ]),
                          );
                        }
                      : null,
                ),
              ],
            ),
          );
        }

        return Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            spacing: 12,
            children: [
              if (title.isNotEmpty)
                Text(
                  title,
                  style: TextStyle(
                    fontSize: 14.5,
                    fontWeight: FontWeight.w600,
                    color: c.textStrong,
                  ),
                ),
              ...fieldWidgets,
              if (error != null && error.isNotEmpty)
                Text(error, style: TextStyle(fontSize: 12, color: c.error)),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  UiKitButton(
                    label: submitLabel,
                    style: parseButtonStyle('primary', 'accent'),
                    onPressed: isEnabled
                        ? () {
                            final payload = <String, String>{
                              for (final id in nonSecretIds)
                                id: _controllers[id]?.text ?? '',
                            };
                            unawaited(invoke('Submit', [payload]));
                          }
                        : null,
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
