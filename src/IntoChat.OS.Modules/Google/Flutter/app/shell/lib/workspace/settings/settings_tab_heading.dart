import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

/// Title and one-line description at the top of a Settings tab.
class SettingsTabHeading extends StatelessWidget {
  const SettingsTabHeading({
    super.key,
    required this.title,
    required this.description,
  });
  final String title, description;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Padding(
      padding: const EdgeInsets.only(bottom: 24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(title, style: type.heading.copyWith(color: c.strong)),
          const SizedBox(height: 6),
          Text(description, style: type.bodyMuted),
        ],
      ),
    );
  }
}
