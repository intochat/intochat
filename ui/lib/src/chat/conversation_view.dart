import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:gpt_markdown/gpt_markdown.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart' hide Focus;
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/controls.dart';
import '../design/keys.dart';
import '../design/tokens.dart';
import '../nodes/empty_state.dart';
import '../nodes/error_line.dart';
import '../nodes/node_context.dart';
import '../nodes/node_renderer.dart';
import 'ui_copyable_message.dart';

/// Declarative conversation surface rendered from a `ConversationNode`.
///
/// Holds only ephemeral client interaction state (composer draft, focus, and
/// scroll position); all session state lives in the C# brain.
class ConversationView extends StatefulWidget {
  const ConversationView({super.key, required this.node, required this.ctx});

  final ConversationNode node;
  final NodeContext ctx;

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

  @override
  void initState() {
    super.initState();
    _hadWaiting = widget.node.waiting != null;
    _composerFocus.addListener(_onFocusChange);
    _controller.addListener(_onDraftChange);
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      if (widget.node.waiting != null) {
        PromptKeys.focus(_dockFocus);
      }
    });
  }

  @override
  void didUpdateWidget(covariant ConversationView oldWidget) {
    super.didUpdateWidget(oldWidget);
    final hasWaiting = widget.node.waiting != null;
    if (hasWaiting && !_hadWaiting) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted && widget.node.waiting != null) {
          PromptKeys.focus(_dockFocus);
        }
      });
    }
    _hadWaiting = hasWaiting;

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
          duration: IcMotion.fast,
          curve: IcMotion.ease,
        );
      }
    }
  }

  @override
  void dispose() {
    _composerFocus.removeListener(_onFocusChange);
    _controller.removeListener(_onDraftChange);
    _controller.dispose();
    _composerFocus.dispose();
    _dockFocus.dispose();
    _scrollController.dispose();
    super.dispose();
  }

  void _onFocusChange() {
    if (mounted) setState(() {});
  }

  void _onDraftChange() {
    if (mounted) setState(() {});
  }

  bool get _composerEnabled =>
      widget.node.composer.enabled && widget.ctx.interactive && !_submitting;

  Future<void> _submit() async {
    if (!_composerEnabled) return;
    final draft = _controller.text;
    if (draft.trim().isEmpty) return;

    setState(() => _submitting = true);
    try {
      final first = await widget.ctx.activateReporting(
        widget.node.composer.key,
        value: draft,
        context: mounted ? context : null,
      );
      if (first == ActivationOutcome.accepted) {
        if (mounted && _controller.text == draft) {
          _controller.clear();
        }
        return;
      }
      if (first == ActivationOutcome.stale) {
        if (widget.ctx.reload case final reload?) {
          await reload();
        }
        final nextFrame = Completer<void>();
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (!nextFrame.isCompleted) nextFrame.complete();
        });
        WidgetsBinding.instance.scheduleFrame();
        await nextFrame.future;
        if (!mounted) return;
        final second = await widget.ctx.activateReporting(
          widget.node.composer.key,
          value: draft,
          context: context,
        );
        if (second == ActivationOutcome.accepted &&
            mounted &&
            _controller.text == draft) {
          _controller.clear();
        }
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
    unawaited(widget.ctx.activate(stop.key, context: context));
  }

  KeyEventResult _handleConversationKey(FocusNode node, KeyEvent event) {
    if (event is! KeyDownEvent) return KeyEventResult.ignored;
    if (event.logicalKey == LogicalKeyboardKey.escape) {
      final stop = widget.node.stop;
      if (stop != null && stop.enabled && widget.ctx.interactive) {
        _activateStop();
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
    final c = IcTheme.of(context);
    final messages = widget.node.messages;
    final error = widget.node.error;
    final empty = widget.node.empty;
    final waiting = widget.node.waiting;

    final hasTimelineEntries = messages.isNotEmpty || error != null;

    return Focus(
      autofocus: waiting == null,
      onKeyEvent: _handleConversationKey,
      child: ColoredBox(
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
      ),
    );
  }

  Widget _buildTimeline(
    BuildContext context,
    List<MessageNode> messages,
    ErrorLineNode? error,
  ) {
    final hasError = error != null;
    final itemCount = messages.length + (hasError ? 1 : 0);

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
                  child: renderErrorLine(error, widget.ctx.deeper()),
                ),
              ),
            ),
          );
        }

        final msgOffset = hasError ? reversedIndex - 1 : reversedIndex;
        final messageIndex = messages.length - 1 - msgOffset;
        final msg = messages[messageIndex];
        return Padding(
          key: ValueKey('msg:${msg.id}'),
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: _maxColumnWidth),
              child: msg.role == 'user'
                  ? _UserMessageItem(message: msg)
                  : _AssistantMessageItem(
                      message: msg,
                      ctx: widget.ctx.deeper(),
                    ),
            ),
          ),
        );
      },
    );
  }

  Widget _buildBottomDock(BuildContext context, IcColors c, Node? waiting) {
    final stop = widget.node.stop;
    final focused = _composerFocus.hasFocus;
    final canSend = _composerEnabled && _controller.text.trim().isNotEmpty;

    return Container(
      padding: const EdgeInsets.fromLTRB(24, 10, 24, 16),
      decoration: BoxDecoration(
        color: c.surfacePage,
        border: Border(top: BorderSide(color: c.borderHairline, width: 0.5)),
      ),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: _maxColumnWidth),
          child: waiting != null
              ? ConstrainedBox(
                  constraints: const BoxConstraints(maxHeight: 380),
                  child: SingleChildScrollView(
                    child: Semantics(
                      liveRegion: true,
                      label: 'Waiting for your response',
                      child: PromptScope(
                        docked: true,
                        child: PromptKeys(
                          focusNode: _dockFocus,
                          autofocus: true,
                          child: NodeRenderer.render(
                            waiting,
                            widget.ctx.deeper(),
                          ),
                        ),
                      ),
                    ),
                  ),
                )
              : Column(
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
                      duration: IcMotion.fast,
                      padding: const EdgeInsets.fromLTRB(14, 8, 8, 8),
                      decoration: BoxDecoration(
                        color: c.surfaceCard,
                        borderRadius: BorderRadius.circular(IcRadius.panel),
                        border: Border.all(
                          color: focused ? c.accent : c.borderInput,
                        ),
                        boxShadow: [
                          if (focused)
                            BoxShadow(color: c.ring, spreadRadius: 3),
                          ...IcShadow.sm(c),
                        ],
                      ),
                      child: Row(
                        crossAxisAlignment: CrossAxisAlignment.end,
                        children: [
                          Expanded(
                            child: Padding(
                              padding: const EdgeInsets.symmetric(vertical: 6),
                              child: Focus(
                                onKeyEvent: _handleComposerKey,
                                child: TextField(
                                  controller: _controller,
                                  focusNode: _composerFocus,
                                  enabled:
                                      widget.node.composer.enabled &&
                                      widget.ctx.interactive,
                                  minLines: 1,
                                  maxLines: 6,
                                  keyboardType: TextInputType.multiline,
                                  textInputAction: TextInputAction.newline,
                                  style: IcType.text(
                                    14,
                                    color: c.textStrong,
                                    height: 1.45,
                                  ),
                                  decoration: InputDecoration.collapsed(
                                    hintText: widget.node.composer.placeholder,
                                    hintStyle: IcType.text(
                                      14,
                                      color: c.textSubtle,
                                      height: 1.45,
                                    ),
                                  ),
                                ),
                              ),
                            ),
                          ),
                          const SizedBox(width: 8),
                          _SendIconButton(
                            enabled: canSend,
                            busy: _submitting,
                            onTap: _submit,
                          ),
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

class _UserMessageItem extends StatelessWidget {
  const _UserMessageItem({required this.message});

  final MessageNode message;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final sending = message.status == 'sending';
    final bubbleBg = c.isDark ? c.textStrong : c.surfaceInk;
    final bubbleFg = c.isDark ? c.surfaceInk : c.onInk;

    return Align(
      alignment: Alignment.centerRight,
      child: Opacity(
        opacity: sending ? 0.6 : 1.0,
        child: LayoutBuilder(
          builder: (context, box) => ConstrainedBox(
            constraints: BoxConstraints(
              maxWidth: box.maxWidth.isFinite ? box.maxWidth * 0.78 : 520,
            ),
            child: UiCopyableMessage(
              copyText: (_) => message.text,
              child: Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 16,
                  vertical: 11,
                ),
                decoration: BoxDecoration(
                  color: bubbleBg,
                  borderRadius: const BorderRadius.only(
                    topLeft: Radius.circular(IcRadius.bubble),
                    topRight: Radius.circular(IcRadius.bubble),
                    bottomLeft: Radius.circular(IcRadius.bubble),
                    bottomRight: Radius.circular(6),
                  ),
                ),
                child: Text(
                  message.text,
                  style: IcType.text(14, color: bubbleFg, height: 1.5),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _AssistantMessageItem extends StatelessWidget {
  const _AssistantMessageItem({required this.message, required this.ctx});

  final MessageNode message;
  final NodeContext ctx;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final activities = message.activities ?? const <ActivityNode>[];
    final cards = message.cards ?? const <Node>[];
    final isRunning = message.status == 'running';
    final model = message.model;
    final retry = message.retry;

    return Align(
      alignment: Alignment.centerLeft,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 680),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
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
            if (message.text.isNotEmpty || isRunning)
              UiCopyableMessage(
                copyText: (_) => message.text,
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.center,
                  children: [
                    if (message.text.isNotEmpty)
                      Flexible(
                        child: GptMarkdown(
                          message.text,
                          style: IcType.text(
                            14,
                            color: c.textBody,
                            height: 1.6,
                          ),
                        ),
                      ),
                    if (isRunning) ...[
                      if (message.text.isNotEmpty) const SizedBox(width: 8),
                      WorkingSpinner(size: 14, color: c.working),
                    ],
                  ],
                ),
              ),
            if (cards.isNotEmpty) ...[
              if (message.text.isNotEmpty || isRunning)
                const SizedBox(height: 10),
              for (var i = 0; i < cards.length; i++) ...[
                if (i > 0) const SizedBox(height: 10),
                NodeRenderer.render(cards[i], ctx),
              ],
            ],
            if ((model != null && model.isNotEmpty) || retry != null) ...[
              const SizedBox(height: 6),
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (model != null && model.isNotEmpty)
                    Text(model, style: IcType.text(12, color: c.textMuted)),
                  if (retry != null) ...[
                    if (model != null && model.isNotEmpty)
                      const SizedBox(width: 10),
                    Btn(
                      label: retry.label,
                      icon: LucideIcons.rotateCcw,
                      small: true,
                      onTap: retry.enabled && ctx.interactive
                          ? () => ctx.activate(retry.key, context: context)
                          : null,
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
    final c = IcTheme.of(context);
    final running =
        activity.status == 'running' || activity.status == 'working';
    final failed = activity.status == 'failed' || activity.status == 'error';
    final fg = failed ? c.error : (running ? c.working : c.textMuted);
    final bg = failed ? c.errorBg : (running ? c.workingBg : c.surfaceSunk);
    final detail = activity.detail;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(IcRadius.xs),
        border: Border.all(color: fg.withValues(alpha: 0.22)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (running)
            WorkingSpinner(size: 12, color: fg)
          else
            Icon(
              failed ? LucideIcons.circleAlert : LucideIcons.check,
              size: 12,
              color: fg,
            ),
          const SizedBox(width: 6),
          Flexible(
            child: Text(
              detail != null && detail.isNotEmpty
                  ? '${activity.title} · $detail'
                  : activity.title,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: IcType.text(12, weight: FontWeight.w500, color: fg),
            ),
          ),
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
    final c = IcTheme.of(context);
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
              borderRadius: BorderRadius.circular(IcRadius.button),
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
                    style: IcType.text(
                      13,
                      weight: FontWeight.w600,
                      color: c.textStrong,
                    ),
                  ),
                ),
                const SizedBox(width: 10),
                Text(
                  'Esc to stop',
                  style: IcType.text(
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

class _SendIconButton extends StatelessWidget {
  const _SendIconButton({
    required this.enabled,
    required this.busy,
    required this.onTap,
  });

  final bool enabled;
  final bool busy;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Semantics(
      button: true,
      enabled: enabled,
      label: 'Send message',
      child: MouseRegion(
        cursor: enabled ? SystemMouseCursors.click : MouseCursor.defer,
        child: GestureDetector(
          onTap: enabled ? onTap : null,
          child: AnimatedContainer(
            duration: IcMotion.fast,
            width: 34,
            height: 34,
            decoration: BoxDecoration(
              color: enabled ? c.accent : c.surfaceSunk,
              borderRadius: BorderRadius.circular(IcRadius.button),
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
