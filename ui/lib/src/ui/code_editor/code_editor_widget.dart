import 'dart:async';
import 'package:flutter/material.dart';
import 'package:re_editor/re_editor.dart';

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
  final CodeLineEditingController _controller = CodeLineEditingController();
  int _syncedVersion = -1;
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
        final wordWrap = state['wordWrap'] == true;
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
                  Container(
                    padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text(language.toUpperCase(), style: TextStyle(fontSize: 10, fontWeight: FontWeight.w600, color: c.textMuted)),
                  ),
                  const Spacer(),
                  if (!readOnly)
                    TextButton.icon(
                      onPressed: () {
                        _debounce?.cancel();
                        unawaited(invoke('SaveContent', const []));
                      },
                      icon: const Icon(Icons.save_outlined, size: 14),
                      label: const Text('Save', style: TextStyle(fontSize: 12)),
                    ),
                ],
              ),
              const SizedBox(height: 8),
              Container(
                height: 180,
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: c.surfaceSunk,
                  borderRadius: BorderRadius.circular(IcRadius.sm),
                  border: Border.all(color: c.borderHairline),
                ),
                child: CodeEditor(
                  controller: _controller,
                  readOnly: readOnly,
                  wordWrap: wordWrap,
                  style: CodeEditorStyle(
                    fontFamily: 'monospace',
                    fontSize: 12.5,
                    fontHeight: 1.45,
                    textColor: c.textStrong,
                    backgroundColor: c.surfaceSunk,
                    cursorColor: c.accent,
                    selectionColor: c.accent.withValues(alpha: 0.22),
                  ),
                  indicatorBuilder: (context, editingController, chunkController, notifier) {
                    return Row(
                      children: [
                        DefaultCodeLineNumber(
                          controller: editingController,
                          notifier: notifier,
                          textStyle: TextStyle(
                            fontFamily: 'monospace',
                            fontSize: 11,
                            color: c.textMuted,
                          ),
                        ),
                        const SizedBox(width: 8),
                      ],
                    );
                  },
                  onChanged: (value) {
                    if (readOnly) return;
                    final text = _controller.text;
                    _debounce?.cancel();
                    _debounce = Timer(const Duration(milliseconds: 220), () {
                      unawaited(invoke('SetContent', [text]));
                    });
                  },
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}
