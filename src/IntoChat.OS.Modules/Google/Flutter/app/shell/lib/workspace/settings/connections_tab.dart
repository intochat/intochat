import 'package:flutter/material.dart';

import '../../connections/connect_window.dart';
import '../../connections/connections_settings.dart';
import '../../connections/connectors_menu.dart';

/// Connections: the existing connections surface, unchanged.
class ConnectionsTab extends StatelessWidget {
  const ConnectionsTab({
    super.key,
    this.request,
    this.connectorRequest,
    this.windowsRequest,
    this.onOpen,
  });
  final ConnectionsRequest? request, connectorRequest, windowsRequest;
  final OpenUrl? onOpen;

  @override
  Widget build(BuildContext context) => ConnectionsSettings(
    request: request,
    connectorRequest: connectorRequest,
    windowsRequest: windowsRequest,
    onOpen: onOpen,
  );
}
