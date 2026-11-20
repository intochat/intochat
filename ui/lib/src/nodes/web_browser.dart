import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/browser/ui_web_browser.dart';
import 'node_context.dart';

/// The page an app embeds, in the shell's embedded browser at the node's height.
Widget renderWebBrowser(WebBrowserNode node, NodeContext ctx) => Semantics(
  container: true,
  explicitChildNodes: true,
  label: controlNameOf(node.automationName, node.title ?? node.key, node.key),
  child: UiWebBrowser(
    key: ValueKey('browser:${node.key}'),
    part: UiWebBrowserPart(
      name: '${ctx.view}#${node.key}',
      uri: node.url,
      title: node.title ?? node.url,
    ),
    height: node.height.clamp(160, 1600),
  ),
);
