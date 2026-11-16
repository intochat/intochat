import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/secret_card.dart';
import '../theme/wire_node_values.dart';
import 'node_common.dart';

Widget renderSecretCard(SecretCardNode node, NodeContext ctx) => SecretCard(
  source: cardSource(node.savedIn),
  data: SecretRequestData(
    name: node.name,
    why: node.why,
    savedIn: node.savedIn,
  ),
  state: parseSecretState(node.status),
  onEnter: pressOf(node.enter, ctx),
  onSkip: pressOf(node.skip, ctx),
);
