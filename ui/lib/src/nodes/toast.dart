import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../ui/mix/ui_mix_toast.dart';

Widget renderToast(ToastNode node, NodeContext ctx) =>
    KitToastBody(message: node.message);
