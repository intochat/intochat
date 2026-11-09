import 'package:flutter/material.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

import 'node_context.dart';
import '../components/kit/kit_toast.dart';

Widget renderToast(ToastNode node, NodeContext ctx) =>
    KitToastBody(message: node.message);
