import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/ui_kit_surface.dart';

/// The page an app embeds, named; the live browser is the `webbrowser` host surface.
Widget renderWebBrowser(WebBrowserNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final text = Theme.of(context).textTheme;
    return UiKitSurface(
      key: ValueKey('browser:${node.key}'),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        spacing: 4,
        children: [
          Text(node.title ?? node.url, style: text.titleSmall),
          SelectableText(node.url, style: text.bodySmall),
        ],
      ),
    );
  },
);
