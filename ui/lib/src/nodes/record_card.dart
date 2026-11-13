import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/record_card.dart';
import 'node_common.dart';

Widget renderRecordCard(RecordCardNode node, NodeContext ctx) => RecordCard(
  source: cardSource(node.objectLabel),
  data: RecordData(
    objectLabel: node.objectLabel,
    name: node.name,
    subtitle: node.subtitle,
    linkLabel: node.linkLabel,
    fields: [
      for (final field in node.fields)
        RecordField(field.label, field.value, warn: field.warn),
    ],
  ),
  onOpen: pressOf(node.open, ctx),
);
