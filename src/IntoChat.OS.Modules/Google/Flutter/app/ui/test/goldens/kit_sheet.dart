import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

/// A fixed sheet of kit components for golden comparison across palettes.
/// Chat bubbles are drawn to the Figma spec (user = strong, 18/18/6/18);
/// they are not the flutter_chat_ui bubbles.
class KitSheet extends StatelessWidget {
  const KitSheet({super.key});

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    Widget section(String title, List<Widget> children) => Padding(
      padding: const EdgeInsets.only(bottom: 20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(title, style: type.meta),
          const SizedBox(height: 8),
          ...children,
        ],
      ),
    );
    Widget bubble(String text, {required bool user}) => Align(
      alignment: user ? Alignment.centerRight : Alignment.centerLeft,
      child: Container(
        margin: const EdgeInsets.only(bottom: 8),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
        decoration: BoxDecoration(
          color: user ? c.strong : c.surface,
          border: user ? null : Border.all(color: c.hairline),
          borderRadius: BorderRadius.only(
            topLeft: const Radius.circular(18),
            topRight: const Radius.circular(18),
            bottomLeft: Radius.circular(user ? 18 : 6),
            bottomRight: Radius.circular(user ? 6 : 18),
          ),
        ),
        child: Text(
          text,
          style: type.body.copyWith(color: user ? c.page : c.body),
        ),
      ),
    );

    final left = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('Heading 800 Ag', style: type.heading),
        const SizedBox(height: 8),
        Text('Body 400 - the quick brown fox jumps', style: type.body),
        const SizedBox(height: 4),
        Text(
          'META 500 - 12:04 - run-42',
          style: type.meta.copyWith(fontSize: 14, color: c.strong),
        ),
        Text(
          'META 400 - 12:04 - run-42',
          style: UiType.withWeightAxis(
            type.meta.copyWith(
              fontSize: 14,
              color: c.strong,
              fontWeight: FontWeight.w400,
            ),
          ),
        ),
        const SizedBox(height: 20),
        section('BUTTONS', [
          Wrap(
            spacing: 12,
            children: [
              FilledButton(onPressed: () {}, child: const Text('Primary')),
              OutlinedButton(onPressed: () {}, child: const Text('Secondary')),
              FilledButton(
                style: FilledButton.styleFrom(
                  backgroundColor: c.error,
                  foregroundColor: c.onAccent,
                ),
                onPressed: () {},
                child: const Text('Delete'),
              ),
            ],
          ),
        ]),
        section('TEXT FIELD', [
          const TextField(decoration: InputDecoration(labelText: 'Name')),
          const SizedBox(height: 12),
          const TextField(
            decoration: InputDecoration(
              labelText: 'Email',
              errorText: 'Enter a valid email',
            ),
          ),
        ]),
        section('SELECT', [
          DropdownButtonFormField<String>(
            initialValue: 'Weekly',
            items: const [
              DropdownMenuItem(value: 'Weekly', child: Text('Weekly')),
              DropdownMenuItem(value: 'Daily', child: Text('Daily')),
            ],
            onChanged: (_) {},
          ),
        ]),
        section('TABS', [
          const DefaultTabController(
            length: 3,
            child: TabBar(
              isScrollable: true,
              tabAlignment: TabAlignment.start,
              tabs: [
                Tab(text: 'Overview'),
                Tab(text: 'Runs'),
                Tab(text: 'Settings'),
              ],
            ),
          ),
        ]),
      ],
    );

    final right = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        section('CARD', [
          Card(
            margin: EdgeInsets.zero,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Quarterly report', style: type.cardTitle),
                  const SizedBox(height: 4),
                  Text('Updated 2 hours ago', style: type.bodyMuted),
                ],
              ),
            ),
          ),
        ]),
        section('TOGGLE / PROGRESS', [
          const UiToggle(label: 'Notifications', on: true),
          const UiToggle(label: 'Auto-sync', on: false),
          const SizedBox(height: 8),
          const UiProgress(
            determinate: true,
            value: 0.4,
            label: 'Uploading 40%',
          ),
        ]),
        section('INFOBARS', [
          for (final s in ['info', 'success', 'warning', 'error'])
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: UiInfoBar(
                severity: s,
                title: s.toUpperCase(),
                body: 'Severity $s message.',
              ),
            ),
        ]),
        section('CHAT', [
          bubble('Help me organize these files.', user: true),
          bubble(
            'Choose a folder and I will suggest a structure.',
            user: false,
          ),
        ]),
      ],
    );

    return ColoredBox(
      color: c.page,
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(child: left),
            const SizedBox(width: 48),
            Expanded(child: right),
          ],
        ),
      ),
    );
  }
}
