import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:gpt_markdown/gpt_markdown.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart' hide Focus;
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/controls.dart';
import '../design/keys.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_tokens.dart';
import '../nodes/button.dart';
import '../nodes/empty_state.dart';
import '../nodes/error_line.dart';
import '../nodes/node_common.dart';
import '../nodes/node_context.dart';
import '../nodes/node_renderer.dart';
import '../theme/brand_visuals.dart';
import '../theme/wire_node_values.dart';
import '../widgets/voice/audio_out.dart';
import '../widgets/voice/mic_stream.dart';
import '../widgets/voice/talk_socket.dart';
import '../widgets/voice/ui_voice_input.dart';
import '../widgets/voice/voice_mode_view.dart';
import 'model_label.dart';
import 'model_picker.dart';
import 'ui_copyable_message.dart';

/// Declarative conversation surface rendered from a `ConversationNode`.
///
/// Holds only ephemeral client interaction state (composer draft, focus, and
/// scroll position); all session state lives in the C# brain.
class ConversationView extends StatefulWidget {
  const ConversationView({
    super.key,
    required this.node,
    required this.ctx,
    this.onErrorAction,
    this.models,
    this.activeModelId,
    this.selectModel,
    this.pinModel,
    this.readAloud = false,
    this.talkConversation,
    this.deltas,
    this.readConversation,
  });

  final ConversationNode node;
  final NodeContext ctx;

  /// Runs the error line's action instead of sending its command (an action
  /// the child's parent decided, which the thread's view does not hold).
  final VoidCallback? onErrorAction;

  /// Model choices shown in the composer's model picker popover.
  final List<ModelChoice>? models;

  /// Wire id of the currently selected model for highlighting in the picker.
  final String? activeModelId;

  /// Sends its command with the chosen model id to switch this conversation.
  final ModelSelectNode? selectModel;

  /// Sends its command with the chosen model id to pin it as the Chat default.
  final ModelSelectNode? pinModel;

  /// When true (and [NodeContext.speak] is available), completed assistant
  /// replies expose a read-aloud control beside the copy action.
  final bool readAloud;

  /// Conversation id that can open full-duplex voice mode when [NodeContext.talk]
  /// is available; null hides the voice-mode button.
  final String? talkConversation;

  /// Ephemeral `TextDelta` signal payloads (`{id, entry, delta, at}`).
  final Stream<Map<String, dynamic>>? deltas;

  /// Reads the full `ConversationView` (`IConversation.Read(sinceRevision: null)`)
  /// when a delta offset indicates a gap.
  final Future<Map<String, dynamic>?> Function()? readConversation;

  @override
  State<ConversationView> createState() => _ConversationViewState();
}

class _ConversationViewState extends State<ConversationView> {
  static const double _maxColumnWidth = 760;

  final TextEditingController _controller = TextEditingController();
  final FocusNode _composerFocus = FocusNode(
    debugLabel: 'ConversationComposer',
  );
  final FocusNode _dockFocus = FocusNode(debugLabel: 'ConversationWaitingDock');
  final ScrollController _scrollController = ScrollController();

  bool _submitting = false;
  bool _hadWaiting = false;
  bool _suppressWaitingDefault = false;

  StreamSubscription<Map<String, dynamic>>? _deltaSub;
  ({String entry, String text})? _pending;
  final List<WireTextDelta> _queuedDeltas = <WireTextDelta>[];
  bool _healing = false;
  int _healEpoch = 0;

  AudioOut? _readAloudOut;
  String? _readingMessageId;
  int _readAloudEpoch = 0;

  _VoiceSession? _voiceSession;

  @override
  void initState() {
    super.initState();
    _hadWaiting = widget.node.waiting != null;
    _composerFocus.addListener(_onFocusChange);
    _controller.addListener(_onDraftChange);
    _subscribeDeltas();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      if (widget.node.waiting != null && !_suppressWaitingDefault) {
        PromptKeys.focus(_dockFocus);
      }
    });
  }

  @override
  void didUpdateWidget(covariant ConversationView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.deltas != oldWidget.deltas) {
      _clearPending();
      _subscribeDeltas();
    } else {
      _syncPendingWithNode();
    }
    final hasWaiting = widget.node.waiting != null;
    if (hasWaiting && !_hadWaiting) {
      // Never steal focus or preselect the default option when a prompt arrives over an active draft.
      final hasDraft = _controller.text.isNotEmpty;
      _suppressWaitingDefault = hasDraft;
      if (!hasDraft) {
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted &&
              widget.node.waiting != null &&
              !_suppressWaitingDefault) {
            PromptKeys.focus(_dockFocus);
          }
        });
      }
    } else if (!hasWaiting && _hadWaiting) {
      _suppressWaitingDefault = false;
    }
    _hadWaiting = hasWaiting;

    final nextTalkConversation = widget.talkConversation?.trim();
    if (_voiceSession != null &&
        (widget.ctx.talk == null ||
            nextTalkConversation == null ||
            nextTalkConversation.isEmpty ||
            _voiceSession!.conversation != nextTalkConversation)) {
      _voiceSession = null;
    }

    final addedMessage =
        widget.node.messages.length > oldWidget.node.messages.length ||
        (widget.node.messages.isNotEmpty &&
            oldWidget.node.messages.isNotEmpty &&
            widget.node.messages.last.id != oldWidget.node.messages.last.id);
    if (addedMessage && _scrollController.hasClients) {
      // In a reversed ListView, offset 0.0 is the bottom (latest message).
      if (_scrollController.offset <= 64) {
        _scrollController.animateTo(
          0,
          duration: UiTokens.of(context).fast,
          curve: UiTokens.of(context).ease,
        );
      }
    }
  }

  @override
  void dispose() {
    _readAloudEpoch++;
    final readAloudOut = _readAloudOut;
    _readAloudOut = null;
    if (readAloudOut != null) {
      unawaited(readAloudOut.stop().catchError((Object _) {}));
    }
    _deltaSub?.cancel();
    _composerFocus.removeListener(_onFocusChange);
    _controller.removeListener(_onDraftChange);
    _controller.dispose();
    _composerFocus.dispose();
    _dockFocus.dispose();
    _scrollController.dispose();
    super.dispose();
  }

  void _subscribeDeltas() {
    _deltaSub?.cancel();
    _deltaSub = widget.deltas?.listen(_onDelta, onError: (Object _) {});
  }

  void _clearPending() {
    _pending = null;
    _queuedDeltas.clear();
    _healing = false;
    _healEpoch++;
  }

  MessageNode? _findMessage(String id) {
    for (final msg in widget.node.messages) {
      if (msg.id == id) return msg;
    }
    return null;
  }

  void _syncPendingWithNode() {
    final pending = _pending;
    if (pending == null) return;
    final msg = _findMessage(pending.entry);
    if (msg == null) return;
    if (parseMessageStatus(msg.status) != WireMessageStatus.running) {
      _clearPending();
      return;
    }
    if (msg.text.length > pending.text.length) {
      _pending = (entry: msg.id, text: msg.text);
    }
  }

  void _onDelta(Map<String, dynamic> payload) {
    final parsed = parseTextDelta(payload);
    if (parsed == null || parsed.delta.isEmpty) return;

    final existing = _findMessage(parsed.entry);
    if (existing != null &&
        parseMessageStatus(existing.status) != WireMessageStatus.running) {
      return;
    }

    if (_pending == null || _pending!.entry != parsed.entry) {
      _queuedDeltas.clear();
      _healing = false;
      _healEpoch++;
      _pending = (entry: parsed.entry, text: existing?.text ?? '');
    }

    if (_healing) {
      _queuedDeltas.add(parsed);
      return;
    }

    final current = _pending!.text;
    if (parsed.at > current.length) {
      _queuedDeltas.add(parsed);
      unawaited(_healGap(parsed.entry));
      return;
    }

    if (_applyDelta(parsed) && mounted) {
      setState(() {});
    }
  }

  bool _applyDelta(WireTextDelta delta) {
    final pending = _pending;
    if (pending == null || pending.entry != delta.entry) return false;
    final current = pending.text;
    if (delta.at == current.length) {
      _pending = (entry: pending.entry, text: '$current${delta.delta}');
      return true;
    }
    if (delta.at < current.length) {
      final overlap = current.length - delta.at;
      if (overlap < delta.delta.length) {
        final suffix = delta.delta.substring(overlap);
        _pending = (entry: pending.entry, text: '$current$suffix');
        return true;
      }
    }
    return false;
  }

  Future<void> _healGap(String entry) async {
    final reader = widget.readConversation;
    if (reader == null) {
      _queuedDeltas.clear();
      return;
    }
    _healing = true;
    final epoch = _healEpoch;
    try {
      final view = await reader();
      if (!mounted || epoch != _healEpoch) return;
      final reply = parseActiveConversationReply(view, entry);
      var changed = false;
      if (reply != null &&
          _pending != null &&
          _pending!.entry == entry &&
          reply.length > _pending!.text.length) {
        _pending = (entry: entry, text: reply);
        changed = true;
      }
      _queuedDeltas.sort((a, b) => a.at.compareTo(b.at));
      final queued = List<WireTextDelta>.of(_queuedDeltas);
      _queuedDeltas.clear();
      for (final delta in queued) {
        if (_applyDelta(delta)) changed = true;
      }
      if (changed && mounted) setState(() {});
    } catch (_) {
      if (epoch == _healEpoch) {
        _queuedDeltas.clear();
      }
    } finally {
      if (epoch == _healEpoch) {
        _healing = false;
      }
    }
  }

  void _onFocusChange() {
    if (mounted) setState(() {});
  }

  void _onDraftChange() {
    if (mounted) setState(() {});
  }

  void _appendDictationDraft(String text) {
    final trimmed = text.trim();
    if (trimmed.isEmpty) return;
    final existing = _controller.text;
    final next = existing.isEmpty
        ? trimmed
        : existing.endsWith(' ') || existing.endsWith('\n')
        ? '$existing$trimmed'
        : '$existing $trimmed';
    _controller.value = TextEditingValue(
      text: next,
      selection: TextSelection.collapsed(offset: next.length),
    );
    _composerFocus.requestFocus();
  }

  bool get _composerEnabled =>
      widget.node.composer.enabled && widget.ctx.interactive && !_submitting;

  Future<void> _submit() async {
    if (!_composerEnabled) return;
    final draft = _controller.text;
    if (draft.trim().isEmpty) return;

    setState(() => _submitting = true);
    try {
      final composer = widget.node.composer;
      final sent = await pressCommand(
        composer.key,
        composer.onSubmit,
        widget.ctx,
        mounted ? context : null,
        value: draft,
      );
      // A refused send keeps the draft; the host shows the refusal.
      if (sent == ActivationOutcome.accepted &&
          mounted &&
          _controller.text == draft) {
        _controller.clear();
      }
    } finally {
      if (mounted) {
        setState(() => _submitting = false);
      }
    }
  }

  void _activateStop() {
    final stop = widget.node.stop;
    if (stop == null || !stop.enabled || !widget.ctx.interactive) return;
    unawaited(pressCommand(stop.key, stop.command, widget.ctx, context));
  }

  Future<void> _stopReadAloud() async {
    _readAloudEpoch++;
    if (mounted && _readingMessageId != null) {
      setState(() => _readingMessageId = null);
    }
    final out = _readAloudOut;
    if (out != null) {
      await out.stop().catchError((Object _) {});
    }
  }

  Future<void> _toggleReadAloud(String messageId, String text) async {
    final speak = widget.ctx.speak;
    if (speak == null) return;
    if (_readingMessageId == messageId) {
      await _stopReadAloud();
      return;
    }
    final trimmed = text.trim();
    if (trimmed.isEmpty) return;
    final epoch = ++_readAloudEpoch;
    await _readAloudOut?.stop().catchError((Object _) {});
    if (!mounted || epoch != _readAloudEpoch) return;
    setState(() => _readingMessageId = messageId);
    final out = _readAloudOut ??=
        (widget.ctx.createAudioOut ?? SoLoudAudioOut.new)();
    try {
      final wav = await speak(trimmed);
      if (!mounted || epoch != _readAloudEpoch) return;
      await out.playClip(wav);
    } catch (_) {
      // Recover button state cleanly when synthesis or playback fails.
    } finally {
      if (mounted && epoch == _readAloudEpoch) {
        setState(() => _readingMessageId = null);
      }
    }
  }

  void _toggleVoiceMode(String conversation) {
    if (_voiceSession != null) {
      _closeVoiceMode();
      return;
    }
    final talk = widget.ctx.talk;
    if (talk == null) return;
    unawaited(_stopReadAloud());
    final mic = (widget.ctx.createMicStream ?? RecordMicStream.new)();
    final out = (widget.ctx.createAudioOut ?? SoLoudAudioOut.new)();
    setState(() {
      _voiceSession = _VoiceSession(
        conversation: conversation,
        connect: () => talk(conversation),
        mic: mic,
        out: out,
      );
    });
  }

  void _closeVoiceMode() {
    if (_voiceSession == null) return;
    if (mounted) {
      setState(() => _voiceSession = null);
    } else {
      _voiceSession = null;
    }
  }

  // Sends the picker's command with the model id as its value.
  Future<void> _selectModel(ModelSelectNode picker, String modelId) async {
    if (!widget.ctx.interactive || modelId.trim().isEmpty) return;
    await pressCommand(
      picker.key,
      picker.onSubmit,
      widget.ctx,
      mounted ? context : null,
      value: modelId,
    );
  }

  KeyEventResult _handleConversationKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent) return KeyEventResult.ignored;
    if (event.logicalKey == LogicalKeyboardKey.escape) {
      final stop = widget.node.stop;
      if (stop != null && stop.enabled && widget.ctx.interactive) {
        _activateStop();
        return KeyEventResult.handled;
      }
      if (_voiceSession != null) {
        _closeVoiceMode();
        return KeyEventResult.handled;
      }
    }
    return KeyEventResult.ignored;
  }

  KeyEventResult _handleComposerKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent && event is! KeyRepeatEvent) {
      return KeyEventResult.ignored;
    }
    final key = event.logicalKey;
    final isEnter =
        key == LogicalKeyboardKey.enter ||
        key == LogicalKeyboardKey.numpadEnter;
    if (isEnter && !HardwareKeyboard.instance.isShiftPressed) {
      if (event is KeyDownEvent) {
        unawaited(_submit());
      }
      return KeyEventResult.handled;
    }
    return KeyEventResult.ignored;
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final messages = widget.node.messages;
    final error = widget.node.error;
    final empty = widget.node.empty;
    final waiting = widget.node.waiting;

    final hasTimelineEntries = messages.isNotEmpty || error != null;

    final content = ColoredBox(
      color: c.surfacePage,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Expanded(
            child: hasTimelineEntries
                ? _buildTimeline(context, messages, error)
                : (empty != null
                      ? renderEmptyState(empty, widget.ctx.deeper())
                      : const SizedBox.shrink()),
          ),
          _buildBottomDock(context, c, waiting),
        ],
      ),
    );

    return Focus(
      autofocus: waiting == null,
      onKeyEvent: _handleConversationKey,
      child: LayoutBuilder(
        builder: (context, constraints) => constraints.hasBoundedHeight
            ? content
            : SizedBox(height: 520, child: content),
      ),
    );
  }

  Widget _buildTimeline(
    BuildContext context,
    List<MessageNode> messages,
    ErrorLineNode? error,
  ) {
    final visibleMessages = <MessageNode>[
      for (final msg in messages)
        if (!_isEmptyFailedAssistantStub(msg)) msg,
    ];
    final hasError = error != null;
    final itemCount = visibleMessages.length + (hasError ? 1 : 0);
    final modelNames = _buildModelNames(widget.models);

    return ListView.builder(
      controller: _scrollController,
      reverse: true,
      padding: const EdgeInsets.fromLTRB(24, 24, 24, 16),
      itemCount: itemCount,
      itemBuilder: (context, reversedIndex) {
        if (hasError && reversedIndex == 0) {
          return Padding(
            padding: const EdgeInsets.only(top: 12),
            child: Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: _maxColumnWidth),
                child: Align(
                  alignment: Alignment.centerLeft,
                  child: ErrorLineView(
                    node: error,
                    ctx: widget.ctx.deeper(),
                    onAction: widget.onErrorAction,
                  ),
                ),
              ),
            ),
          );
        }

        final msgOffset = hasError ? reversedIndex - 1 : reversedIndex;
        final messageIndex = visibleMessages.length - 1 - msgOffset;
        final msg = visibleMessages[messageIndex];
        final pending = _pending;
        final pendingText =
            pending != null &&
                pending.entry == msg.id &&
                parseMessageStatus(msg.status) == WireMessageStatus.running &&
                pending.text.length > msg.text.length
            ? pending.text
            : null;
        final sender = msg.sender;
        final isSelf = sender != null
            ? sender.kind == SenderKind.self
            : parseMessageRole(msg.role) == WireMessageRole.user;
        final isOtherHuman = sender != null && sender.kind == SenderKind.human;
        return Padding(
          key: ValueKey('msg:${msg.id}'),
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: _maxColumnWidth),
              child: isSelf
                  ? _UserMessageItem(message: msg, ctx: widget.ctx.deeper())
                  : isOtherHuman
                  ? _OtherHumanMessageItem(
                      message: msg,
                      sender: sender,
                      ctx: widget.ctx.deeper(),
                    )
                  : _AssistantMessageItem(
                      message: msg,
                      ctx: widget.ctx.deeper(),
                      modelNames: modelNames,
                      textOverride: pendingText,
                      readAloud: widget.readAloud,
                      reading: _readingMessageId == msg.id,
                      onReadAloud: (text) =>
                          unawaited(_toggleReadAloud(msg.id, text)),
                    ),
            ),
          ),
        );
      },
    );
  }

  static Map<String, String> _buildModelNames(List<ModelChoice>? choices) {
    if (choices == null || choices.isEmpty) return const <String, String>{};
    final names = <String, String>{};
    for (final choice in choices) {
      names[choice.id] = choice.label;
      final parsed = parseDeviceModelId(choice.id);
      if (parsed != null) {
        names.putIfAbsent(parsed.modelId, () => choice.label);
      }
    }
    return names;
  }

  static bool _isEmptyFailedAssistantStub(MessageNode msg) {
    if (parseMessageRole(msg.role) != WireMessageRole.assistant) return false;
    final status = parseMessageStatus(msg.status);
    if (status != WireMessageStatus.failed &&
        status != WireMessageStatus.interrupted) {
      return false;
    }
    if (msg.text.trim().isNotEmpty) return false;
    if (msg.cards != null && msg.cards!.isNotEmpty) return false;
    if (msg.activities != null && msg.activities!.isNotEmpty) return false;
    if (msg.retry != null) return false;
    if (msg.error != null) return false;
    return true;
  }

  Widget _buildBottomDock(BuildContext context, UiColors c, Node? waiting) {
    final stop = widget.node.stop;
    final composer = widget.node.composer;
    final focused = _composerFocus.hasFocus;
    final canSend = _composerEnabled && _controller.text.trim().isNotEmpty;
    final hasActiveModel =
        (composer.activeModelLabel != null &&
            composer.activeModelLabel!.trim().isNotEmpty) ||
        (composer.activeProviderIcon != null &&
            composer.activeProviderIcon!.trim().isNotEmpty);
    final models = widget.models ?? const <ModelChoice>[];
    final showModelChip = hasActiveModel || models.isNotEmpty;
    final rawTalkConversation = widget.talkConversation?.trim();
    final talkConversation =
        rawTalkConversation != null && rawTalkConversation.isNotEmpty
        ? rawTalkConversation
        : null;
    final showVoiceMode = talkConversation != null && widget.ctx.talk != null;

    return Container(
      padding: const EdgeInsets.fromLTRB(24, 10, 24, 16),
      decoration: BoxDecoration(
        color: c.surfacePage,
        border: Border(top: BorderSide(color: c.borderHairline, width: 0.5)),
      ),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: _maxColumnWidth),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (_voiceSession case final voice?)
                Padding(
                  padding: const EdgeInsets.only(bottom: 10),
                  child: VoiceModeView(
                    key: ValueKey('voice-hud:${voice.conversation}'),
                    connect: voice.connect,
                    mic: voice.mic,
                    out: voice.out,
                    onClose: _closeVoiceMode,
                  ),
                ),
              if (waiting != null)
                ConstrainedBox(
                  constraints: const BoxConstraints(maxHeight: 380),
                  child: SingleChildScrollView(
                    child: Semantics(
                      liveRegion: true,
                      label: 'Waiting for your response',
                      child: PromptScope(
                        docked: true,
                        suppressDefault: _suppressWaitingDefault,
                        child: PromptKeys(
                          focusNode: _dockFocus,
                          autofocus: !_suppressWaitingDefault,
                          defaultSelection: !_suppressWaitingDefault,
                          child: NodeRenderer.render(
                            waiting,
                            widget.ctx.deeper(),
                          ),
                        ),
                      ),
                    ),
                  ),
                )
              else
                Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    if (stop != null)
                      _StopHintBar(
                        label: stop.label,
                        enabled: stop.enabled && widget.ctx.interactive,
                        onTap: _activateStop,
                      ),
                    AnimatedContainer(
                      duration: UiTokens.of(context).fast,
                      padding: const EdgeInsets.fromLTRB(14, 8, 8, 8),
                      decoration: BoxDecoration(
                        color: c.surfaceCard,
                        borderRadius: BorderRadius.circular(
                          UiTokens.of(context).panel,
                        ),
                        border: Border.all(
                          color: focused ? c.accent : c.borderInput,
                        ),
                        boxShadow: [
                          if (focused)
                            BoxShadow(color: c.ring, spreadRadius: 3),
                          ...UiShadows.sm(c),
                        ],
                      ),
                      child: Row(
                        crossAxisAlignment: CrossAxisAlignment.end,
                        children: [
                          Expanded(
                            child: Focus(
                              onKeyEvent: _handleComposerKey,
                              child: TextField(
                                controller: _controller,
                                focusNode: _composerFocus,
                                enabled:
                                    composer.enabled && widget.ctx.interactive,
                                minLines: 1,
                                maxLines: 6,
                                keyboardType: TextInputType.multiline,
                                textInputAction: TextInputAction.newline,
                                style: UiType.textStyle(
                                  14,
                                  color: c.textStrong,
                                  height: 1.45,
                                ),
                                decoration: InputDecoration(
                                  isDense: true,
                                  filled: false,
                                  border: InputBorder.none,
                                  enabledBorder: InputBorder.none,
                                  focusedBorder: InputBorder.none,
                                  disabledBorder: InputBorder.none,
                                  contentPadding: const EdgeInsets.symmetric(
                                    vertical: 7,
                                  ),
                                  hintText: composer.placeholder,
                                  hintStyle: UiType.textStyle(
                                    14,
                                    color: c.textSubtle,
                                    height: 1.45,
                                  ),
                                ),
                              ),
                            ),
                          ),
                          if (showModelChip) ...[
                            const SizedBox(width: 8),
                            SizedBox(
                              height: 34,
                              child: Center(
                                child: ModelPickerButton(
                                  key: const ValueKey('composer-active-model'),
                                  models: models,
                                  activeModelId: widget.activeModelId,
                                  activeModelLabel: composer.activeModelLabel,
                                  activeProviderIcon:
                                      composer.activeProviderIcon,
                                  enabled: widget.ctx.interactive,
                                  onSelect: widget.selectModel == null
                                      ? null
                                      : (id) => unawaited(
                                          _selectModel(widget.selectModel!, id),
                                        ),
                                  onPin: widget.pinModel == null
                                      ? null
                                      : (id) => unawaited(
                                          _selectModel(widget.pinModel!, id),
                                        ),
                                ),
                              ),
                            ),
                          ],
                          if (composer.dictation) ...[
                            const SizedBox(width: 8),
                            UiVoiceInput(
                              enabled: _composerEnabled,
                              onTranscribe: widget.ctx.transcribe,
                              onDraft: _appendDictationDraft,
                            ),
                          ],
                          if (showVoiceMode) ...[
                            const SizedBox(width: 8),
                            _VoiceModeIconButton(
                              key: const ValueKey('composer-voice-mode'),
                              enabled: widget.ctx.enabled,
                              active: _voiceSession != null,
                              onTap: () => _toggleVoiceMode(talkConversation),
                            ),
                          ],
                          const SizedBox(width: 8),
                          _SendIconButton(
                            key: const ValueKey('composer-send'),
                            enabled: canSend,
                            busy: _submitting,
                            onTap: _submit,
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _UserMessageItem extends StatelessWidget {
  const _UserMessageItem({required this.message, required this.ctx});

  final MessageNode message;
  final NodeContext ctx;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final sending =
        parseMessageStatus(message.status) == WireMessageStatus.sending;
    final bubbleBg = c.bubbleMine;
    final bubbleFg = c.onBubbleMine;
    final cards = message.cards ?? const <Node>[];

    return Align(
      alignment: Alignment.centerRight,
      child: Opacity(
        opacity: sending ? 0.6 : 1.0,
        child: LayoutBuilder(
          builder: (context, box) => ConstrainedBox(
            constraints: BoxConstraints(
              maxWidth: box.maxWidth.isFinite ? box.maxWidth * 0.78 : 520,
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                if (message.text.isNotEmpty)
                  UiCopyableMessage(
                    copyText: (_) => message.text,
                    child: Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 16,
                        vertical: 11,
                      ),
                      decoration: BoxDecoration(
                        color: bubbleBg,
                        borderRadius: BorderRadius.only(
                          topLeft: Radius.circular(UiTokens.of(context).bubble),
                          topRight: Radius.circular(
                            UiTokens.of(context).bubble,
                          ),
                          bottomLeft: Radius.circular(
                            UiTokens.of(context).bubble,
                          ),
                          bottomRight: const Radius.circular(6),
                        ),
                      ),
                      child: Text(
                        message.text,
                        style: UiType.textStyle(
                          14,
                          color: bubbleFg,
                          height: 1.5,
                        ),
                      ),
                    ),
                  ),
                if (cards.isNotEmpty) ...[
                  if (message.text.isNotEmpty) const SizedBox(height: 8),
                  for (var i = 0; i < cards.length; i++) ...[
                    if (i > 0) const SizedBox(height: 8),
                    NodeRenderer.render(cards[i], ctx),
                  ],
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _OtherHumanMessageItem extends StatelessWidget {
  const _OtherHumanMessageItem({
    required this.message,
    required this.sender,
    required this.ctx,
  });

  final MessageNode message;
  final SenderIdentity sender;
  final NodeContext ctx;

  static String _initials(String name) {
    final trimmed = name.trim();
    if (trimmed.isEmpty) return '?';
    final first = trimmed.substring(0, 1);
    final space = trimmed.lastIndexOf(' ');
    if (space > 0 && space + 1 < trimmed.length) {
      return '$first${trimmed.substring(space + 1, space + 2)}'.toUpperCase();
    }
    return first.toUpperCase();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final displayName = sender.displayName.trim().isNotEmpty
        ? sender.displayName.trim()
        : sender.accountId;
    final cards = message.cards ?? const <Node>[];

    return Align(
      key: ValueKey('msg-human:${message.id}'),
      alignment: Alignment.centerLeft,
      child: LayoutBuilder(
        builder: (context, box) => ConstrainedBox(
          constraints: BoxConstraints(
            maxWidth: box.maxWidth.isFinite ? box.maxWidth * 0.78 : 520,
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Container(
                key: ValueKey('msg-avatar:${message.id}'),
                width: 28,
                height: 28,
                margin: const EdgeInsets.only(top: 18, right: 10),
                decoration: BoxDecoration(
                  color: c.accentTint,
                  shape: BoxShape.circle,
                  border: Border.all(color: c.accentBorder),
                ),
                child: Center(
                  child: Text(
                    _initials(displayName),
                    style: UiType.textStyle(
                      11,
                      weight: FontWeight.w600,
                      color: c.accent,
                    ),
                  ),
                ),
              ),
              Flexible(
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Padding(
                      padding: const EdgeInsets.only(left: 2, bottom: 4),
                      child: Text(
                        displayName,
                        key: ValueKey('msg-speaker:${message.id}'),
                        style: UiType.textStyle(
                          12,
                          weight: FontWeight.w600,
                          color: c.textMuted,
                        ),
                      ),
                    ),
                    if (message.text.isNotEmpty)
                      UiCopyableMessage(
                        copyText: (_) => message.text,
                        child: Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 11,
                          ),
                          decoration: BoxDecoration(
                            color: c.surfaceCard,
                            border: Border.all(color: c.borderHairline),
                            borderRadius: BorderRadius.only(
                              topLeft: Radius.circular(tokens.bubble),
                              topRight: Radius.circular(tokens.bubble),
                              bottomRight: Radius.circular(tokens.bubble),
                              bottomLeft: const Radius.circular(6),
                            ),
                          ),
                          child: Text(
                            message.text,
                            style: UiType.textStyle(
                              14,
                              color: c.textStrong,
                              height: 1.5,
                            ),
                          ),
                        ),
                      ),
                    if (cards.isNotEmpty) ...[
                      if (message.text.isNotEmpty) const SizedBox(height: 8),
                      for (var i = 0; i < cards.length; i++) ...[
                        if (i > 0) const SizedBox(height: 8),
                        NodeRenderer.render(cards[i], ctx),
                      ],
                    ],
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _AssistantMessageItem extends StatelessWidget {
  const _AssistantMessageItem({
    required this.message,
    required this.ctx,
    this.modelNames = const <String, String>{},
    this.textOverride,
    this.readAloud = false,
    this.reading = false,
    this.onReadAloud,
  });

  final MessageNode message;
  final NodeContext ctx;
  final Map<String, String> modelNames;
  final String? textOverride;
  final bool readAloud;
  final bool reading;
  final ValueChanged<String>? onReadAloud;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final text = textOverride ?? message.text;
    final activities = message.activities ?? const <ActivityNode>[];
    final cards = message.cards ?? const <Node>[];
    final isRunning =
        parseMessageStatus(message.status) == WireMessageStatus.running;
    final model = message.model;
    final explicitModelLabel = message.modelLabel;
    final providerIcon = message.providerIcon;
    final hasModelInfo =
        (model != null && model.trim().isNotEmpty) ||
        (explicitModelLabel != null && explicitModelLabel.trim().isNotEmpty) ||
        (providerIcon != null && providerIcon.trim().isNotEmpty);
    final rawChipModel =
        (explicitModelLabel != null && explicitModelLabel.trim().isNotEmpty)
        ? explicitModelLabel
        : model;
    final formattedChipLabel = rawChipModel != null
        ? modelLabel(rawChipModel, modelNames)
        : null;
    final retry = message.retry;
    final sender = message.sender;
    final showRoomAgentBadge =
        sender != null &&
        sender.kind == SenderKind.agent &&
        sender.displayName.trim().isNotEmpty;
    final agentBadgeLabel =
        sender != null && sender.displayName.trim().isNotEmpty
        ? sender.displayName.trim()
        : '@Ino · Room only';

    return Align(
      alignment: Alignment.centerLeft,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 680),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (showRoomAgentBadge) ...[
              Container(
                key: ValueKey('msg-agent-badge:${message.id}'),
                padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
                decoration: BoxDecoration(
                  color: c.accentTint,
                  borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
                  border: Border.all(color: c.accentBorder),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(LucideIcons.sparkles, size: 12, color: c.accent),
                    const SizedBox(width: 5),
                    Text(
                      agentBadgeLabel,
                      style: UiType.textStyle(
                        11.5,
                        weight: FontWeight.w600,
                        color: c.accent,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 6),
            ],
            if (activities.isNotEmpty) ...[
              Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  for (final act in activities) _ActivityPill(activity: act),
                ],
              ),
              const SizedBox(height: 8),
            ],
            if (text.isNotEmpty || isRunning)
              UiCopyableMessage(
                copyText: (_) => text,
                keepActionsVisible: reading,
                actions: [
                  if (readAloud &&
                      ctx.speak != null &&
                      !isRunning &&
                      text.trim().isNotEmpty)
                    _ReadAloudActionButton(
                      key: ValueKey('message-read-aloud:${message.id}'),
                      playing: reading,
                      onTap: () => onReadAloud?.call(text),
                    ),
                ],
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.center,
                  children: [
                    if (text.isNotEmpty)
                      Flexible(
                        child: GptMarkdown(
                          text,
                          style: UiType.textStyle(
                            14,
                            color: c.textBody,
                            height: 1.6,
                          ),
                        ),
                      ),
                    if (isRunning) ...[
                      if (text.isNotEmpty) const SizedBox(width: 8),
                      WorkingSpinner(size: 14, color: c.working),
                    ],
                  ],
                ),
              ),
            if (cards.isNotEmpty) ...[
              if (text.isNotEmpty || isRunning) const SizedBox(height: 10),
              for (var i = 0; i < cards.length; i++) ...[
                if (i > 0) const SizedBox(height: 10),
                NodeRenderer.render(cards[i], ctx),
              ],
            ],
            if (message.error case final error?) ...[
              if (text.isNotEmpty || cards.isNotEmpty)
                const SizedBox(height: 8),
              // Why the reply failed; the retry below asks for this turn again.
              ErrorLineView(
                key: ValueKey('msg-error:${message.id}'),
                node: ErrorLineNode(
                  what: error.what,
                  detail: error.detail,
                  action: null,
                ),
                ctx: ctx,
              ),
            ],
            if (hasModelInfo || retry != null) ...[
              const SizedBox(height: 6),
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (hasModelInfo)
                    Flexible(
                      child: IcBrandChip(
                        key: ValueKey('msg-model:${message.id}'),
                        spec: BrandVisuals.fromNode(
                          label: formattedChipLabel,
                          iconKey: providerIcon,
                        ),
                      ),
                    ),
                  if (retry != null) ...[
                    if (hasModelInfo) const SizedBox(width: 10),
                    keyedControl(
                      retry.key,
                      Btn(
                        label: retry.label,
                        icon: isRegenerateRetryKey(retry.key)
                            ? LucideIcons.refreshCw
                            : LucideIcons.rotateCcw,
                        small: true,
                        onTap: retry.enabled && ctx.interactive
                            ? () => pressCommand(
                                retry.key,
                                retry.command,
                                ctx,
                                context,
                              )
                            : null,
                      ),
                    ),
                  ],
                ],
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _ActivityPill extends StatelessWidget {
  const _ActivityPill({required this.activity});

  final ActivityNode activity;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final status = parseActivityStatus(activity.status);
    final running = status == WireActivityStatus.running;
    final failed = status == WireActivityStatus.failed;
    final fg = failed ? c.error : (running ? c.working : c.textMuted);
    final bg = failed ? c.errorBg : (running ? c.workingBg : c.surfaceSunk);
    final brandSpec = BrandVisuals.tryForIconKey(
      activity.icon,
      label: activity.title,
    );
    final primaryLabel = activity.title.trim();
    final rawDetail = activity.detail?.trim();
    final secondaryDetail = (rawDetail != null && rawDetail.isNotEmpty)
        ? rawDetail
        : null;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
        border: Border.all(
          color: (brandSpec != null && !failed && !running)
              ? (brandSpec.accent ?? c.accent).withValues(alpha: 0.28)
              : fg.withValues(alpha: 0.22),
        ),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (brandSpec != null) ...[
            IcBrandIcon(spec: brandSpec, size: 14),
            const SizedBox(width: 6),
          ],
          if (running) ...[
            WorkingSpinner(size: 11, color: fg),
            const SizedBox(width: 5),
          ] else if (failed) ...[
            Icon(LucideIcons.circleAlert, size: 12, color: fg),
            const SizedBox(width: 5),
          ] else if (brandSpec == null) ...[
            Icon(LucideIcons.check, size: 12, color: fg),
            const SizedBox(width: 6),
          ],
          Flexible(
            child: Text(
              primaryLabel,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: UiType.textStyle(
                12,
                weight: brandSpec != null ? FontWeight.w600 : FontWeight.w500,
                color: (brandSpec != null && !failed) ? c.textStrong : fg,
              ),
            ),
          ),
          if (secondaryDetail != null && secondaryDetail.isNotEmpty) ...[
            const SizedBox(width: 5),
            Flexible(
              child: Text(
                '· $secondaryDetail',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(12, weight: FontWeight.w500, color: fg),
              ),
            ),
          ],
          if (brandSpec != null && !running && !failed) ...[
            const SizedBox(width: 5),
            Icon(LucideIcons.check, size: 11, color: fg),
          ],
        ],
      ),
    );
  }
}

class _StopHintBar extends StatelessWidget {
  const _StopHintBar({
    required this.label,
    required this.enabled,
    required this.onTap,
  });

  final String label;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: MouseRegion(
        cursor: enabled ? SystemMouseCursors.click : MouseCursor.defer,
        child: GestureDetector(
          onTap: enabled ? onTap : null,
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            decoration: BoxDecoration(
              color: c.surfaceCard,
              borderRadius: BorderRadius.circular(UiTokens.of(context).button),
              border: Border.all(color: c.borderHairline),
            ),
            child: Row(
              children: [
                WorkingSpinner(size: 13, color: c.textBody),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    label.isEmpty ? 'Working…' : label,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: UiType.textStyle(
                      13,
                      weight: FontWeight.w600,
                      color: c.textStrong,
                    ),
                  ),
                ),
                const SizedBox(width: 10),
                Text(
                  'Esc to stop',
                  style: UiType.textStyle(
                    12.5,
                    weight: FontWeight.w600,
                    color: c.textMuted,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _ReadAloudActionButton extends StatelessWidget {
  const _ReadAloudActionButton({
    super.key,
    required this.playing,
    required this.onTap,
  });

  final bool playing;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Semantics(
      container: true,
      button: true,
      label: playing ? 'Stop reading aloud' : 'Read aloud',
      child: Material(
        color: colors.surfaceContainerHigh.withValues(alpha: 0.92),
        shape: const CircleBorder(),
        child: InkWell(
          customBorder: const CircleBorder(),
          onTap: onTap,
          child: Tooltip(
            message: playing ? 'Stop reading' : 'Read aloud',
            child: Padding(
              padding: const EdgeInsets.all(6),
              child: Icon(
                playing ? LucideIcons.square : LucideIcons.volume2,
                size: 14,
                color: playing ? colors.primary : colors.onSurfaceVariant,
              ),
            ),
          ),
        ),
      ),
    );
  }
}

final class _VoiceSession {
  const _VoiceSession({
    required this.conversation,
    required this.connect,
    required this.mic,
    required this.out,
  });

  final String conversation;
  final Future<TalkSocket> Function() connect;
  final MicStream mic;
  final AudioOut out;
}

class _VoiceModeIconButton extends StatelessWidget {
  const _VoiceModeIconButton({
    super.key,
    required this.enabled,
    required this.onTap,
    this.active = false,
  });

  final bool enabled;
  final bool active;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return Semantics(
      container: true,
      button: true,
      enabled: enabled,
      toggled: active,
      label: active ? 'Close voice mode' : 'Voice mode',
      child: Tooltip(
        message: active ? 'Close voice mode' : 'Voice mode',
        child: MouseRegion(
          cursor: enabled ? SystemMouseCursors.click : MouseCursor.defer,
          child: GestureDetector(
            onTap: enabled ? onTap : null,
            child: AnimatedContainer(
              duration: UiTokens.of(context).fast,
              width: 34,
              height: 34,
              decoration: BoxDecoration(
                color: active ? c.accentTint : c.surfaceSunk,
                borderRadius: BorderRadius.circular(
                  UiTokens.of(context).button,
                ),
                border: Border.all(
                  color: active ? c.accentBorder : c.borderHairline,
                ),
              ),
              child: Center(
                child: Icon(
                  LucideIcons.audioLines,
                  size: 16,
                  color: active
                      ? c.accent
                      : (enabled ? c.textStrong : c.textSubtle),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _SendIconButton extends StatelessWidget {
  const _SendIconButton({
    super.key,
    required this.enabled,
    required this.busy,
    required this.onTap,
  });

  final bool enabled;
  final bool busy;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    // Its own node: without one, the button role, label and disabled state merge
    // into the enclosing node and mark the whole thread a disabled button.
    return Semantics(
      container: true,
      button: true,
      enabled: enabled,
      label: 'Send message',
      child: MouseRegion(
        cursor: enabled ? SystemMouseCursors.click : MouseCursor.defer,
        child: GestureDetector(
          onTap: enabled ? onTap : null,
          child: AnimatedContainer(
            duration: UiTokens.of(context).fast,
            width: 34,
            height: 34,
            decoration: BoxDecoration(
              color: enabled ? c.accent : c.surfaceSunk,
              borderRadius: BorderRadius.circular(UiTokens.of(context).button),
            ),
            child: Center(
              child: busy
                  ? WorkingSpinner(size: 14, color: c.onAccent)
                  : Icon(
                      LucideIcons.arrowUp,
                      size: 16,
                      color: enabled ? c.onAccent : c.textSubtle,
                    ),
            ),
          ),
        ),
      ),
    );
  }
}
