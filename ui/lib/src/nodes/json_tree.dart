import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/json/ui_json_tree.dart';
import 'node_context.dart';

/// Read-only JSON as a tree; expanding and filtering are the viewer's own and
/// are never sent.
Widget renderJsonTree(JsonTreeNode node, NodeContext ctx) =>
    UiJsonTree(title: node.title, json: node.json);
