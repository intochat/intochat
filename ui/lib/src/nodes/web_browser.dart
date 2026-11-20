import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/browser/ui_web_browser.dart';
import 'node_context.dart';

/// The page an app embeds, in the shell's embedded browser, stretching to
/// fill bounded viewport height and falling back to `node.height` when unbounded.
Widget renderWebBrowser(WebBrowserNode node, NodeContext ctx) => Semantics(
  container: true,
  explicitChildNodes: true,
  label: controlNameOf(node.automationName, node.title ?? node.key, node.key),
  child: LayoutBuilder(
    builder: (context, constraints) {
      final effectiveHeight =
          constraints.hasBoundedHeight && constraints.maxHeight.isFinite
          ? constraints.maxHeight
          : node.height.clamp(160.0, 1600.0);
      return UiWebBrowser(
        key: ValueKey('browser:${node.key}'),
        part: UiWebBrowserPart(
          name: '${ctx.view}#${node.key}',
          uri: node.url,
          title: node.title ?? node.url,
        ),
        height: effectiveHeight,
      );
    },
  ),
);
