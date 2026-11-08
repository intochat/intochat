import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';

/// A bare intent is what a button performs; on its own it draws nothing.
Widget renderIntent(IntentNode node, NodeContext ctx) =>
    const SizedBox.shrink();
