import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../chat/conversation_view.dart';
import 'node_context.dart';

Widget renderConversation(ConversationNode node, NodeContext ctx) => Semantics(
  container: true,
  explicitChildNodes: true,
  label: controlNameOf(node.automationName, node.key, node.key),
  child: ConversationView(node: node, ctx: ctx),
);
