import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import '../../connections/connectors_menu.dart';
import 'settings_tab_heading.dart';

/// Danger zone: delegated actions only. There is no brain deletion, and no
/// desktop layout reset exists in features/os/Desktop to trigger.
class DangerTab extends StatelessWidget {
  const DangerTab({super.key, this.accountBase, this.onOpen});

  /// The kernel's base URI; the cloud account page is `/account/profile` on
  /// its origin.
  final Uri? accountBase;
  final OpenUrl? onOpen;

  @override
  Widget build(BuildContext context) {
    final base = accountBase;
    final open = onOpen;
    final c = UiColors.of(context);
    final type = UiType.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const SettingsTabHeading(
          title: 'Danger zone',
          description: 'Actions that change access to your account.',
        ),
        Container(
          padding: const EdgeInsets.all(11),
          decoration: BoxDecoration(
            color: c.surface,
            border: Border.all(color: c.hairline),
            borderRadius: BorderRadius.circular(
              UiTokens.of(context).controlRadius,
            ),
          ),
          child: Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Delete account',
                      style: type.body.copyWith(
                        color: c.strong,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                    Text(
                      'Opens your account page, where deletion is managed.',
                      style: type.bodyMuted,
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 12),
              UiKitButton(
                key: const Key('settings-delete-account'),
                label: 'Delete account',
                style: UiKitButtonStyle.danger,
                compact: true,
                onPressed: base == null || open == null
                    ? null
                    : () => open(Uri.parse('${base.origin}/account/profile')),
              ),
            ],
          ),
        ),
      ],
    );
  }
}
