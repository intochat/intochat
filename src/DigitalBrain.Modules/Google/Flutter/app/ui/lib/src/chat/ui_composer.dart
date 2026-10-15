import 'dart:convert';
import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../voice_input/ui_voice_input.dart';

class UiComposer extends StatefulWidget {
  const UiComposer({
    super.key,
    required this.state,
    required this.dispatch,
    this.enabled = true,
  });
  final Map<String, dynamic> state;
  final Future<void> Function(Map<String, dynamic>) dispatch;
  final bool enabled;

  @override
  State<UiComposer> createState() => _UiComposerState();
}

class _UiComposerState extends State<UiComposer> {
  late final _text = TextEditingController(
    text: widget.state['draft'] as String? ?? '',
  );
  final _focus = FocusNode();
  final _random = Random.secure();
  int _edits = 0;
  int _editVersion = 0;
  bool _sending = false;
  bool _unsaved = false;
  String? _captureId, _error;

  String _id() {
    final bytes = List<int>.generate(16, (_) => _random.nextInt(256));
    bytes[6] = (bytes[6] & 15) | 64;
    bytes[8] = (bytes[8] & 63) | 128;
    final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
    return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
  }

  Future<void> _event(
    String action, {
    String? value,
    String? id,
    String? mimeType,
  }) => widget.dispatch({
    'action': action,
    'context': widget.state['context'],
    'selectionRevision': widget.state['generation'],
    'value': value,
    'operationId': id,
    'mimeType': mimeType,
  });

  Future<void> _edit(String value) async {
    await _saveEdit(value);
  }

  Future<bool> _saveEdit(String value) async {
    final version = ++_editVersion;
    final context = widget.state['context'];
    final generation = widget.state['generation'];
    bool current() =>
        mounted &&
        version == _editVersion &&
        context == widget.state['context'] &&
        generation == widget.state['generation'];
    _edits++;
    _unsaved = true;
    try {
      await _event('edit', value: value);
      if (!current()) return false;
      setState(() {
        _unsaved = false;
        _error = null;
      });
      return true;
    } catch (error) {
      if (current()) setState(() => _error = '$error');
      return false;
    } finally {
      _edits--;
    }
  }

  Future<void> _submit() async {
    if (_sending || !widget.enabled || _text.text.trim().isEmpty) return;
    final value = _text.text;
    final context = widget.state['context'];
    final generation = widget.state['generation'];
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      // Retry an unacknowledged edit before asking the neuron to capture it.
      if (_unsaved) {
        if (!await _saveEdit(value)) return;
      }
      if (!mounted ||
          context != widget.state['context'] ||
          generation != widget.state['generation']) {
        return;
      }
      await _event('submit', value: value, id: _id());
    } catch (error) {
      if (mounted) setState(() => _error = '$error');
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  @override
  void didUpdateWidget(UiComposer oldWidget) {
    super.didUpdateWidget(oldWidget);
    final draft = widget.state['draft'] as String? ?? '';
    if (_edits == 0 && !_unsaved && _text.text != draft) {
      _text.value = TextEditingValue(
        text: draft,
        selection: TextSelection.collapsed(offset: draft.length),
      );
    }
  }

  @override
  void dispose() {
    _text.dispose();
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final voice = widget.state['voice'] as Map?;
    final status = voice?['status'];
    final transcribing = status == 'transcribing';
    final message =
        _error ??
        widget.state['error'] as String? ??
        voice?['error'] as String? ??
        switch (status) {
          'transcribing' => 'Transcribing… You can cancel or keep editing.',
          'ready' => 'Transcript ready. Review and edit it, then Send.',
          'cancelled' => 'Recording cancelled. Your draft is unchanged.',
          _ => null,
        };
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Expanded(
            child: Focus(
              onKeyEvent: (_, event) {
                if (event is KeyDownEvent &&
                    event.logicalKey == LogicalKeyboardKey.enter &&
                    !HardwareKeyboard.instance.isShiftPressed &&
                    !_text.value.composing.isValid) {
                  _submit();
                  return KeyEventResult.handled;
                }
                return KeyEventResult.ignored;
              },
              child: TextField(
                controller: _text,
                focusNode: _focus,
                enabled: widget.enabled,
                onChanged: _edit,
                expands: true,
                minLines: null,
                maxLines: null,
                maxLength: 32000,
                textAlignVertical: TextAlignVertical.top,
                keyboardType: TextInputType.multiline,
                textInputAction: TextInputAction.newline,
                decoration: InputDecoration(
                  labelText: widget.state['label'] as String? ?? 'Message',
                  counterText: '',
                  border: const OutlineInputBorder(),
                ),
              ),
            ),
          ),
          if (message != null)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Tooltip(
                message: message,
                child: Text(
                  message,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            ),
          Row(
            children: [
              if (transcribing) ...[
                const SizedBox.square(
                  dimension: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
                IconButton(
                  tooltip: 'Cancel transcription',
                  onPressed: () =>
                      _event('voice-cancel', id: voice!['id'] as String),
                  icon: const Icon(Icons.close),
                ),
              ] else
                UiVoiceInput(
                  key: ValueKey(widget.state['context']),
                  enabled: widget.enabled,
                  label: 'Record a voice draft',
                  onBegin: () async {
                    setState(() => _error = null);
                    _captureId = _id();
                    await _event('voice-begin', id: _captureId);
                  },
                  onCancel: () async {
                    if (_captureId != null) {
                      await _event('voice-cancel', id: _captureId);
                    }
                  },
                  onCaptureError: (error) async {
                    const message =
                        'Voice input is unavailable. Your draft is unchanged. '
                        'Check your microphone and connection, then try again or type your message.';
                    if (mounted) setState(() => _error = message);
                    if (_captureId != null) {
                      await _event(
                        'voice-error',
                        id: _captureId,
                        value: message,
                      );
                    }
                  },
                  onAudio: (audio, mimeType) => _event(
                    'voice-audio',
                    id: _captureId,
                    value: base64Encode(audio),
                    mimeType: mimeType,
                  ),
                ),
              const Spacer(),
              FilledButton.icon(
                onPressed: widget.enabled && !_sending ? _submit : null,
                icon: const Icon(Icons.arrow_upward, size: 18),
                label: const Text('Send'),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
