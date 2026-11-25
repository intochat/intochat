import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/image/ui_image_viewer.dart';
import 'node_context.dart';

/// An image node: a URL or a `data:image/*` URL. Zoom and rotation are the
/// viewer's own and are never sent.
Widget renderImage(ImageNode node, NodeContext ctx) => UiImageViewer(
  url: node.url,
  caption: node.caption,
  height: node.height,
  fit: switch (node.fit) {
    ImageFit.contain => BoxFit.contain,
    ImageFit.cover => BoxFit.cover,
  },
);
