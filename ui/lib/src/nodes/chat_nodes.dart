import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../chat/conversation_view.dart';
import 'node_context.dart';

// --- src/nodes/conversation.dart ---
Widget renderConversation(ConversationNode node, NodeContext ctx) => Semantics(
  container: true,
  explicitChildNodes: true,
  label: controlNameOf(node.automationName, node.key, node.key),
  child: ConversationView(node: node, ctx: ctx),
);

// --- src/nodes/chat_thread_view.dart ---
const _conversationContract =
    'IntoChat.OS.Modules.Flutter.Conversations.IConversation';
const _textDeltaSignal = 'TextDelta';
const _readMethod = 'Read';

/// What a chat thread neuron draws: the messages (a failed reply with its
/// reason and a retry), composer, stop and retry from the neuron's view; the
/// placeholder, model, status, empty text and error from the child's props.
/// It reuses the `conversation` node's renderer. The typed text stays here
/// until it is submitted.
Widget renderChatThreadView(ChatThreadViewNode node, NodeContext ctx) {
  final props = ChatThreadProps.fromJson(ctx.childProps);
  return Semantics(
    container: true,
    explicitChildNodes: true,
    label: props.automationName ?? node.key,
    child: _ChatThreadSurface(node: node, props: props, ctx: ctx),
  );
}

class _ChatThreadSurface extends StatefulWidget {
  const _ChatThreadSurface({
    required this.node,
    required this.props,
    required this.ctx,
  });

  final ChatThreadViewNode node;
  final ChatThreadProps props;
  final NodeContext ctx;

  @override
  State<_ChatThreadSurface> createState() => _ChatThreadSurfaceState();
}

class _ChatThreadSurfaceState extends State<_ChatThreadSurface> {
  String? _watchedConversation;
  Stream<Map<String, dynamic>>? _deltas;

  String? get _conversationKey {
    final raw = widget.props.conversation?.trim();
    return raw == null || raw.isEmpty ? null : raw;
  }

  void _syncDeltaStream() {
    final conversation = _conversationKey;
    final watcher = widget.ctx.watchSignal;
    if (conversation == null || watcher == null) {
      _watchedConversation = null;
      _deltas = null;
      return;
    }
    if (_deltas != null && _watchedConversation == conversation) {
      return;
    }
    _watchedConversation = conversation;
    _deltas = watcher(_conversationContract, conversation, _textDeltaSignal);
  }

  @override
  void initState() {
    super.initState();
    _syncDeltaStream();
  }

  @override
  void didUpdateWidget(covariant _ChatThreadSurface oldWidget) {
    super.didUpdateWidget(oldWidget);
    _syncDeltaStream();
  }

  @override
  Widget build(BuildContext context) {
    final node = widget.node;
    final props = widget.props;
    final ctx = widget.ctx;
    final error = props.error;
    final open = error?.open;
    final empty = props.emptyText;
    final conversation = _conversationKey;
    final callNeuron = ctx.callNeuron;

    return ConversationView(
      node: ConversationNode(
        key: node.key,
        messages: node.messages,
        composer: ComposerNode(
          key: node.composer.key,
          onSubmit: node.composer.onSubmit,
          placeholder: props.placeholder,
          enabled: props.canSend && props.isEnabled,
          activeModelLabel: props.activeModelLabel,
          activeProviderIcon: props.activeProviderIcon,
          dictation: props.dictation,
        ),
        waiting: null,
        status: props.status,
        error: error == null
            ? null
            : ErrorLineNode(
                what: error.what,
                detail: error.detail,
                action: _errorAction(node, error),
              ),
        stop: props.working ? node.stop : null,
        empty: node.messages.isEmpty && empty != null
            ? EmptyStateNode(title: empty, body: null, suggestions: null)
            : null,
        automationName: props.automationName ?? node.key,
      ),
      ctx: ctx,
      models: props.models,
      activeModelId: props.activeModelId,
      selectModel: node.selectModel,
      pinModel: node.pinModel,
      readAloud: props.readAloud,
      conversationKey: conversation,
      talkConversation: props.talkConversation,
      deltas: _deltas,
      readConversation: conversation != null && callNeuron != null
          ? () => callNeuron(
              _conversationContract,
              conversation,
              _readMethod,
              const [null],
            )
          : null,
      // A retry is the thread's own node; a window to open is the parent's
      // decision, performed here as a client-local intent.
      onErrorAction: error != null && !error.retry && open != null
          ? () => ctx.perform(
              IntentNode(intentKind: IntentKind.openWindow, arg: open),
            )
          : null,
    );
  }
}

ButtonNode? _errorAction(ChatThreadViewNode node, ChatThreadError error) {
  final label = error.actionLabel;
  if (label == null) return null;
  if (error.retry) {
    // The thread draws its retry node only once its parent's props say the
    // error retries; until then the line offers no action.
    final retry = node.retry;
    if (retry == null) return null;
    return ButtonNode(
      key: retry.key,
      label: label,
      command: retry.command,
      intent: retry.intent,
      enabled: retry.enabled,
      tone: retry.tone,
      tooltip: retry.tooltip,
      visible: retry.visible,
      automationName: label,
      style: retry.style,
      icon: retry.icon,
      small: retry.small,
    );
  }
  final open = error.open;
  if (open == null) return null;
  return ButtonNode(
    key: 'error-open',
    label: label,
    command: null,
    intent: IntentNode(intentKind: IntentKind.openWindow, arg: open),
    enabled: true,
    tone: 'neutral',
    tooltip: null,
    visible: true,
    automationName: label,
    style: '',
    icon: null,
    small: true,
  );
}
