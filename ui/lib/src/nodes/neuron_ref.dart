import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

/// A live neuron surface (a viewport, a web browser) hosted in place; without a host, its address.
Widget renderNeuronRef(NeuronRefNode node, NodeContext ctx) =>
    ctx.reference?.call(node.refKind, node.neuronUri) ??
    Builder(
      builder: (context) {
        final c = UiColors.of(context);
        final tokens = UiTokens.of(context);
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
          decoration: BoxDecoration(
            color: c.recessed,
            borderRadius: BorderRadius.circular(tokens.xs),
            border: Border.all(color: c.hairline),
          ),
          child: SelectableText(
            node.neuronUri,
            style: UiType.tabularStyle(
              12,
              weight: FontWeight.w600,
              color: c.accent,
            ).copyWith(letterSpacing: -0.1),
          ),
        );
      },
    );
