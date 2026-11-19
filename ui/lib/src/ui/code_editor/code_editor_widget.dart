import 'dart:async';
import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

class CodeEditorNeuronWidget extends StatefulWidget {
  const CodeEditorNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<CodeEditorNeuronWidget> createState() => _CodeEditorNeuronWidgetState();
}

class _CodeEditorNeuronWidgetState extends State<CodeEditorNeuronWidget> {
  final TextEditingController _controller = TextEditingController();
  int _syncedVersion = -1;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.code-editor',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final version = (state['version'] as num?)?.toInt() ?? 0;
        final title = (state['title'] as String?) ?? 'untitled';
        final language = (state['language'] as String?) ?? 'text';
        final content = (state['content'] as String?) ?? '';
        final readOnly = state['readOnly'] == true;
        final dirty = state['dirty'] == true;

        if (version != _syncedVersion && _controller.text != content) {
          _syncedVersion = version;
          _controller.text = content;
        }

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
                  Icon(Icons.code, size: 15, color: c.accent),
                  const SizedBox(width: 6),
                  Text(
                    dirty ? '$title •' : title,
                    style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: c.textStrong),
                  ),
                  const SizedBox(width: 8),
                  Text(language.toUpperCase(), style: TextStyle(fontSize: 10, color: c.textMuted)),
                  const Spacer(),
                  if (!readOnly)
                    TextButton.icon(
                      onPressed: () => invoke('SaveContent', const []),
                      icon: const Icon(Icons.save_outlined, size: 14),
                      label: const Text('Save', style: TextStyle(fontSize: 12)),
                    ),
                ],
              ),
              const SizedBox(height: 8),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: c.surfaceSunk,
                  borderRadius: BorderRadius.circular(IcRadius.sm),
                  border: Border.all(color: c.borderHairline),
                ),
                child: TextField(
                  controller: _controller,
                  readOnly: readOnly,
                  maxLines: 8,
                  minLines: 4,
                  style: TextStyle(fontFamily: 'monospace', fontSize: 12, color: c.textStrong),
                  decoration: const InputDecoration.collapsed(hintText: '// Write code…'),
                  onChanged: (value) => invoke('SetContent', [value]),
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}
