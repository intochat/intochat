import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

/// A kit-styled dialog with full-width actions.
class SettingsDialog extends StatelessWidget {
  const SettingsDialog({
    super.key,
    required this.title,
    required this.body,
    required this.actions,
    this.field,
  });
  final String title, body;
  final Widget? field;
  final List<Widget> actions;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Dialog(
      backgroundColor: c.surface,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(UiTokens.of(context).modalRadius),
      ),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(title, style: type.heading.copyWith(color: c.strong)),
              const SizedBox(height: 8),
              Text(body, style: type.bodyMuted),
              if (field != null) ...[const SizedBox(height: 16), field!],
              const SizedBox(height: 20),
              for (final (i, action) in actions.indexed) ...[
                if (i > 0) const SizedBox(height: 8),
                action,
              ],
            ],
          ),
        ),
      ),
    );
  }
}
