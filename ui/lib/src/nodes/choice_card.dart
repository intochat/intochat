import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'button.dart';
import 'node_context.dart';
import '../design/cards/choice_card.dart';
import 'node_common.dart';

Widget renderChoiceCard(ChoiceCardNode node, NodeContext ctx) => ChoiceCard(
  source: cardSource('Ino'),
  question: node.question,
  options: [
    for (final option in node.options)
      ChoiceOption(option.title, detail: option.detail),
  ],
  picked: node.pickedIndex,
  onPick: ctx.interactive && node.pickedIndex == null
      ? (i) {
          final pick = node.options.elementAtOrNull(i)?.pick;
          if (pick != null && pick.enabled) {
            pressCommand(pick.key, pick.command, ctx, null);
          }
        }
      : null,
);
