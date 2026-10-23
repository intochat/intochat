import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'settings_tab_heading.dart';

/// Archived chats: an empty state until the chat list supplies data.
class ArchivedTab extends StatelessWidget {
  const ArchivedTab({super.key});

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      const SettingsTabHeading(
        title: 'Archived chats',
        description: 'Conversations you have archived.',
      ),
      Text('Archived chats appear here.', style: UiType.of(context).bodyMuted),
    ],
  );
}
