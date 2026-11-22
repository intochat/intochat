import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import 'neuron_widget.dart';
import 'ui_neuron_registry.dart';

/// DevUI-grade reactive presenter for `IntoChat.OS.Modules.UI.Chat.IChatThread`.
///
/// Supports:
/// - Collapsible `AiReasoning` thinking blocks
/// - Expandable DevUI tool-call cards (name, status, duration, args JSON, result JSON)
/// - Live inline `Modules.UI` slots (`ui.chart`, `ui.table`, etc.) inside turns
/// - Context & Telemetry header bar (token budget gauge, `/compact` button, context inspector drawer)
/// - Interactive model selector, voice indicator, and streaming send/stop composer
class ChatThreadWidget extends StatefulWidget {
  const ChatThreadWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<ChatThreadWidget> createState() => _ChatThreadWidgetState();
}

class _ChatThreadWidgetState extends State<ChatThreadWidget> {
  final _composer = TextEditingController();
  final _expandedTools = <String>{};
  final _expandedReasoning = <String>{};
  bool _showContextInspector = false;

  @override
  void dispose() {
    _composer.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.chat',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = '${state['title'] ?? 'Ino'}';
        final activeModel = '${state['activeModel'] ?? ''}';
        final working = state['working'] == true;
        final canSend = state['canSend'] != false && !working;
        final error = state['error'] as String?;
        final totalIn = (state['totalInputTokens'] as num?)?.toInt() ?? 0;
        final totalOut = (state['totalOutputTokens'] as num?)?.toInt() ?? 0;
        final budget =
            (state['contextBudgetTokens'] as num?)?.toInt() ?? 128000;
        final usedRatio = budget <= 0
            ? 0.0
            : ((totalIn + totalOut) / budget).clamp(0.0, 1.0);
        final summary = state['contextSummary'] as String?;

        final rawMessages = state['messages'] is List
            ? state['messages'] as List
            : const <dynamic>[];
        final rawContexts = state['contextItems'] is List
            ? state['contextItems'] as List
            : const <dynamic>[];
        final rawModels = state['availableModels'] is List
            ? state['availableModels'] as List
            : const <dynamic>[];

        final messagesBody = Padding(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            spacing: 12,
            children: [
              if (rawMessages.isEmpty)
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 24),
                  child: Center(
                    child: Text(
                      'Ask Ino anything — tool calls, reasoning, and live charts appear inline.',
                      style: TextStyle(fontSize: 13, color: c.textMuted),
                    ),
                  ),
                ),
              for (final raw in rawMessages)
                if (raw is Map) _buildMessage(context, c, raw, invoke),
              if (error != null && error.isNotEmpty)
                Text(error, style: TextStyle(fontSize: 12.5, color: c.error)),
            ],
          ),
        );

        return LayoutBuilder(
          builder: (context, constraints) {
            final stretch =
                constraints.hasBoundedHeight &&
                constraints.maxHeight.isFinite &&
                constraints.maxHeight >= 220;
            return Container(
              decoration: BoxDecoration(
                color: c.surfaceCard,
                borderRadius: BorderRadius.circular(IcRadius.card),
                border: Border.all(color: c.borderHairline),
              ),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                mainAxisSize: stretch ? MainAxisSize.max : MainAxisSize.min,
                children: [
                  // DevUI Telemetry & Context Header
                  Padding(
                    padding: const EdgeInsets.fromLTRB(14, 10, 14, 10),
                    child: Wrap(
                      spacing: 10,
                      runSpacing: 6,
                      crossAxisAlignment: WrapCrossAlignment.center,
                      children: [
                        Text(
                          title,
                          style: TextStyle(
                            fontSize: 14,
                            fontWeight: FontWeight.w700,
                            color: c.textStrong,
                          ),
                        ),
                        if (activeModel.isNotEmpty)
                          Container(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 8,
                              vertical: 3,
                            ),
                            decoration: BoxDecoration(
                              color: c.accent.withValues(alpha: 0.12),
                              borderRadius: BorderRadius.circular(999),
                            ),
                            child: Text(
                              activeModel,
                              style: TextStyle(
                                fontSize: 11.5,
                                fontWeight: FontWeight.w600,
                                color: c.accent,
                              ),
                            ),
                          ),
                        Text(
                          '${totalIn + totalOut} / $budget tok (${(usedRatio * 100).round()}%)',
                          style: TextStyle(
                            fontSize: 11.5,
                            color: c.textMuted,
                            fontFeatures: const [FontFeature.tabularFigures()],
                          ),
                        ),
                        TextButton.icon(
                          onPressed: () => setState(
                            () =>
                                _showContextInspector = !_showContextInspector,
                          ),
                          icon: Icon(
                            LucideIcons.layers,
                            size: 14,
                            color: _showContextInspector
                                ? c.accent
                                : c.textMuted,
                          ),
                          label: Text(
                            'Context (${rawContexts.length})',
                            style: TextStyle(
                              fontSize: 12,
                              color: _showContextInspector
                                  ? c.accent
                                  : c.textBody,
                            ),
                          ),
                        ),
                        TextButton.icon(
                          onPressed: () => unawaited(invoke('Compact')),
                          icon: Icon(
                            LucideIcons.minimize2,
                            size: 14,
                            color: c.textMuted,
                          ),
                          label: Text(
                            'Compact',
                            style: TextStyle(fontSize: 12, color: c.textBody),
                          ),
                        ),
                      ],
                    ),
                  ),
                  if (_showContextInspector) ...[
                    Divider(height: 1, color: c.borderHairline),
                    Container(
                      color: c.surfaceSunk,
                      padding: const EdgeInsets.all(12),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        spacing: 6,
                        children: [
                          Text(
                            'PROVISIONED CONTEXTS & MEMORY',
                            style: TextStyle(
                              fontSize: 10.5,
                              fontWeight: FontWeight.w700,
                              letterSpacing: 0.7,
                              color: c.textMuted,
                            ),
                          ),
                          if (summary != null && summary.isNotEmpty)
                            Text(
                              'Summary: $summary',
                              style: TextStyle(fontSize: 12, color: c.textBody),
                            ),
                          for (final ctxItem in rawContexts)
                            if (ctxItem is Map)
                              Row(
                                children: [
                                  Icon(
                                    LucideIcons.database,
                                    size: 13,
                                    color: c.accent,
                                  ),
                                  const SizedBox(width: 6),
                                  Text(
                                    '${ctxItem['name'] ?? ''}',
                                    style: TextStyle(
                                      fontSize: 12,
                                      fontWeight: FontWeight.w600,
                                      color: c.textStrong,
                                    ),
                                  ),
                                  const SizedBox(width: 8),
                                  Expanded(
                                    child: Text(
                                      '${ctxItem['summary'] ?? ''}',
                                      maxLines: 1,
                                      overflow: TextOverflow.ellipsis,
                                      style: TextStyle(
                                        fontSize: 12,
                                        color: c.textMuted,
                                      ),
                                    ),
                                  ),
                                ],
                              ),
                        ],
                      ),
                    ),
                  ],
                  Divider(height: 1, color: c.borderHairline),
                  if (stretch)
                    Expanded(
                      child: SingleChildScrollView(
                        primary: false,
                        child: messagesBody,
                      ),
                    )
                  else
                    messagesBody,
                  Divider(height: 1, color: c.borderHairline),
                  // Rich Composer
                  Padding(
                    padding: const EdgeInsets.all(12),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      spacing: 8,
                      children: [
                        Row(
                          children: [
                            Expanded(
                              child: TextField(
                                controller: _composer,
                                enabled: canSend,
                                style: TextStyle(
                                  fontSize: 13.5,
                                  color: c.textStrong,
                                ),
                                decoration: InputDecoration(
                                  hintText: working
                                      ? 'Ino is thinking…'
                                      : 'Message Ino… (/compact, @neuron)',
                                  hintStyle: TextStyle(
                                    fontSize: 13,
                                    color: c.textMuted,
                                  ),
                                  isDense: true,
                                  contentPadding: const EdgeInsets.symmetric(
                                    horizontal: 12,
                                    vertical: 10,
                                  ),
                                  border: OutlineInputBorder(
                                    borderRadius: BorderRadius.circular(
                                      IcRadius.sm,
                                    ),
                                    borderSide: BorderSide(
                                      color: c.borderInput,
                                    ),
                                  ),
                                ),
                                onSubmitted: (_) => _submit(invoke),
                              ),
                            ),
                            const SizedBox(width: 8),
                            if (working)
                              FilledButton.icon(
                                onPressed: () => unawaited(invoke('Stop')),
                                icon: const Icon(LucideIcons.square, size: 14),
                                label: const Text('Stop'),
                              )
                            else
                              FilledButton.icon(
                                onPressed: canSend
                                    ? () => _submit(invoke)
                                    : null,
                                icon: const Icon(LucideIcons.send, size: 14),
                                label: const Text('Send'),
                              ),
                          ],
                        ),
                        if (rawModels.isNotEmpty)
                          Wrap(
                            spacing: 6,
                            runSpacing: 4,
                            children: [
                              for (final m in rawModels)
                                if (m is Map)
                                  ChoiceChip(
                                    label: Text(
                                      '${m['label'] ?? m['id'] ?? ''}',
                                      style: const TextStyle(fontSize: 11.5),
                                    ),
                                    selected: '${m['id']}' == activeModel,
                                    onSelected: (_) => unawaited(
                                      invoke('SelectModel', ['${m['id']}']),
                                    ),
                                  ),
                            ],
                          ),
                      ],
                    ),
                  ),
                ],
              ),
            );
          },
        );
      },
    );
  }

  void _submit(
    Future<void> Function(String method, [List<Object?> args]) invoke,
  ) {
    final text = _composer.text.trim();
    if (text.isEmpty) return;
    _composer.clear();
    unawaited(invoke('Submit', [text, const <Object?>[]]));
  }

  Widget _buildMessage(
    BuildContext context,
    IcColors c,
    Map msg,
    Future<void> Function(String method, [List<Object?> args]) invoke,
  ) {
    final id = '${msg['id'] ?? ''}';
    final role = '${msg['role'] ?? 'assistant'}';
    final isUser = role == 'user';
    final text = '${msg['text'] ?? ''}';
    final reasoning = '${msg['reasoning'] ?? ''}'.trim();
    final status = '${msg['status'] ?? 'done'}';
    final err = msg['error'] as String?;
    final toolCalls = msg['toolCalls'] is List
        ? msg['toolCalls'] as List
        : const <dynamic>[];
    final slots = msg['slots'] is List
        ? msg['slots'] as List
        : const <dynamic>[];

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: isUser ? c.accent.withValues(alpha: 0.08) : c.surfaceSunk,
        borderRadius: BorderRadius.circular(IcRadius.sm),
        border: Border.all(color: c.borderHairline),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        spacing: 8,
        children: [
          Row(
            children: [
              Icon(
                isUser ? LucideIcons.user : LucideIcons.sparkles,
                size: 14,
                color: isUser ? c.textStrong : c.accent,
              ),
              const SizedBox(width: 6),
              Text(
                isUser ? 'You' : 'Ino',
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w700,
                  color: c.textStrong,
                ),
              ),
              if (status == 'streaming') ...[
                const SizedBox(width: 8),
                Text(
                  'streaming…',
                  style: TextStyle(fontSize: 11, color: c.accent),
                ),
              ],
            ],
          ),
          // Collapsible AiReasoning block
          if (reasoning.isNotEmpty)
            InkWell(
              onTap: () => setState(() {
                if (!_expandedReasoning.remove(id)) {
                  _expandedReasoning.add(id);
                }
              }),
              child: Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: c.surfaceCard,
                  borderRadius: BorderRadius.circular(6),
                  border: Border.all(color: c.borderHairline),
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  spacing: 4,
                  children: [
                    Row(
                      children: [
                        Icon(LucideIcons.brain, size: 13, color: c.textMuted),
                        const SizedBox(width: 6),
                        Text(
                          _expandedReasoning.contains(id)
                              ? 'Hide reasoning'
                              : 'Show reasoning (${reasoning.length} chars)',
                          style: TextStyle(
                            fontSize: 11.5,
                            fontWeight: FontWeight.w600,
                            color: c.textMuted,
                          ),
                        ),
                      ],
                    ),
                    if (_expandedReasoning.contains(id))
                      Text(
                        reasoning,
                        style: TextStyle(
                          fontSize: 12,
                          height: 1.4,
                          color: c.textMuted,
                        ),
                      ),
                  ],
                ),
              ),
            ),
          // Expandable DevUI Tool-Call Step Cards
          for (final rawCall in toolCalls)
            if (rawCall is Map) _buildToolCallCard(c, rawCall),
          if (text.isNotEmpty)
            SelectableText(
              text,
              style: TextStyle(fontSize: 13.5, height: 1.45, color: c.textBody),
            ),
          // Live inline Modules.UI slots (IChart, ITable, etc.)
          for (final rawSlot in slots)
            if (rawSlot is Map)
              buildUiNeuronWidget(
                    kind: '${rawSlot['kind'] ?? ''}',
                    neuronKey: '${rawSlot['neuronKey'] ?? ''}',
                    callNeuron: widget.callNeuron,
                    watch: widget.watch,
                  ) ??
                  const SizedBox.shrink(),
          if (err != null && err.isNotEmpty)
            Row(
              children: [
                Expanded(
                  child: Text(
                    err,
                    style: TextStyle(fontSize: 12, color: c.error),
                  ),
                ),
                TextButton(
                  onPressed: () => unawaited(invoke('Retry', [id])),
                  child: const Text('Retry'),
                ),
              ],
            ),
        ],
      ),
    );
  }

  Widget _buildToolCallCard(IcColors c, Map call) {
    final callId = '${call['callId'] ?? call['name'] ?? ''}';
    final name = '${call['name'] ?? 'tool'}';
    final status = '${call['status'] ?? 'done'}';
    final durationMs = call['durationMs'];
    final argsJson = '${call['argumentsJson'] ?? '{}'}';
    final resultJson = call['resultJson'] as String?;
    final expanded = _expandedTools.contains(callId);

    return Container(
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: c.borderHairline),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            borderRadius: BorderRadius.circular(8),
            onTap: () => setState(() {
              if (!_expandedTools.remove(callId)) {
                _expandedTools.add(callId);
              }
            }),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
              child: Row(
                children: [
                  Icon(
                    status == 'running'
                        ? LucideIcons.loaderCircle
                        : status == 'error'
                        ? LucideIcons.triangleAlert
                        : LucideIcons.wrench,
                    size: 13,
                    color: status == 'error' ? c.error : c.accent,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      name,
                      style: TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                  if (durationMs != null)
                    Text(
                      '${durationMs}ms',
                      style: TextStyle(
                        fontSize: 11,
                        color: c.textMuted,
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                  const SizedBox(width: 6),
                  Icon(
                    expanded ? LucideIcons.chevronUp : LucideIcons.chevronDown,
                    size: 14,
                    color: c.textMuted,
                  ),
                ],
              ),
            ),
          ),
          if (expanded) ...[
            Divider(height: 1, color: c.borderHairline),
            Padding(
              padding: const EdgeInsets.all(10),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                spacing: 6,
                children: [
                  Text(
                    'Arguments',
                    style: TextStyle(
                      fontSize: 11,
                      fontWeight: FontWeight.w700,
                      color: c.textMuted,
                    ),
                  ),
                  SelectableText(
                    argsJson,
                    style: TextStyle(fontSize: 11.5, color: c.textBody),
                  ),
                  if (resultJson != null && resultJson.isNotEmpty) ...[
                    Text(
                      'Result',
                      style: TextStyle(
                        fontSize: 11,
                        fontWeight: FontWeight.w700,
                        color: c.textMuted,
                      ),
                    ),
                    SelectableText(
                      resultJson,
                      style: TextStyle(fontSize: 11.5, color: c.textBody),
                    ),
                  ],
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }
}
