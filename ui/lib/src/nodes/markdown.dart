import 'package:flutter/material.dart';
import 'package:gpt_markdown/gpt_markdown.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../theme/ui_colors.dart';
import 'node_context.dart';

/// Markdown text, drawn the way chat messages are.
Widget renderMarkdown(MarkdownNode node, NodeContext ctx) => Builder(
  builder: (context) => GptMarkdown(
    node.value,
    style: Theme.of(context).textTheme.bodyMedium
        ?.copyWith(color: UiColors.of(context).textBody),
  ),
);
