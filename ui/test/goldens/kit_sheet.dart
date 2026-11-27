import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

/// A fixed sheet of kit components for golden comparison across palettes,
/// mirroring the Figma `ComponentsPage` sections. Kit primitives are used
/// directly; each overlay demo gets its own [Overlay] so the open menu stays
/// inside its section.
class KitSheet extends StatelessWidget {
  const KitSheet({super.key});

  /// Semantics label of the menu trigger the golden test opens.
  static const menuTrigger = 'Chat actions';

  static Map<String, dynamic> _button(
    String key,
    String text, {
    int style = 0,
    String? icon,
  }) => {
    'kind': 1,
    'key': key,
    'text': text,
    'action': key,
    'style': style,
    'icon': icon,
  };

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    Widget section(String title, List<Widget> children) => Padding(
      padding: const EdgeInsets.only(bottom: 18),
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
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        decoration: BoxDecoration(
          color: user ? c.userBubble : c.assistant,
          border: user ? null : Border.all(color: c.assistantBorder),
          borderRadius: BorderRadius.only(
            topLeft: const Radius.circular(18),
            topRight: const Radius.circular(18),
            bottomLeft: Radius.circular(user ? 18 : 6),
            bottomRight: Radius.circular(user ? 6 : 18),
          ),
        ),
        child: Text(
          text,
          style: type.body.copyWith(color: user ? c.userText : c.assistantText),
        ),
      ),
    );

    const gap = SizedBox(width: 10, height: 10);
    final left = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('Heading 800 Ag', style: type.heading),
        const SizedBox(height: 6),
        Text('Body 400 - the quick brown fox jumps', style: type.body),
        Text('META 500 - 12:04 - run-42', style: type.meta),
        const SizedBox(height: 18),
        section('BUTTONS', [
          Wrap(
            spacing: 10,
            runSpacing: 10,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              UiMixButton.preset(label: 'Continue', onPressed: () {}),
              UiMixButton.preset(
                label: 'Manage',
                style: UiMixButtonStyle.secondary,
                onPressed: () {},
              ),
              UiMixButton.preset(
                label: 'Ghost',
                style: UiMixButtonStyle.ghost,
                onPressed: () {},
              ),
              UiMixButton.preset(
                label: 'Delete',
                style: UiMixButtonStyle.danger,
                onPressed: () {},
              ),
              UiMixButton.preset(
                label: 'Read more',
                style: UiMixButtonStyle.link,
                onPressed: () {},
              ),
              UiMixButton.preset(
                label: 'New chat',
                icon: 'plus',
                onPressed: () {},
              ),
              UiMixButton.preset(
                label: 'Small',
                compact: true,
                onPressed: () {},
              ),
              const UiMixButton.preset(label: 'Disabled'),
            ],
          ),
        ]),
        section('INPUTS', [
          const UiKitField(label: 'Company', placeholder: 'Nordlys AS'),
          gap,
          UiKitField(
            label: 'First name',
            controller: TextEditingController(text: 'Anna'),
          ),
          gap,
          UiKitField(
            label: 'Work email',
            controller: TextEditingController(text: 'anna@'),
            error: 'Enter a complete email address.',
          ),
        ]),
        section('SELECT', [
          UiKitSelect<String>(
            hint: 'Language',
            items: const [
              UiKitSelectItem('en', 'English'),
              UiKitSelectItem('de', 'Deutsch'),
            ],
            value: 'en',
            onChanged: (_) {},
          ),
        ]),
      ],
    );

    final middle = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        section('CARD', [
          UiMixSurface(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Quarterly report', style: type.cardTitle),
                const SizedBox(height: 4),
                Text('Updated 2 hours ago', style: type.bodyMuted),
              ],
            ),
          ),
        ]),
        section('TOGGLE / PROGRESS', [
          UiMixToggle(label: 'Weekly summary', value: true, onChanged: (_) {}),
          const SizedBox(height: 6),
          UiMixToggle(label: 'Auto-sync', value: false, onChanged: (_) {}),
          const SizedBox(height: 6),
          const UiMixProgress(
            determinate: true,
            value: 0.4,
            label: 'Uploading 40%',
          ),
        ]),
        section('BADGES / AVATAR / DIVIDER', [
          const SizedBox(
            height: 64,
            child: _ValueTree({
              'kind': 2,
              'key': 'feedback',
              'children': [
                {
                  'kind': 3,
                  'key': 'badges',
                  'gap': 8,
                  'extents': [100, 56, 76, 60, 34],
                  'children': [
                    {'kind': 10, 'key': 'b0', 'text': 'Coming soon'},
                    {'kind': 10, 'key': 'b1', 'text': 'Done', 'tone': 1},
                    {'kind': 10, 'key': 'b2', 'text': 'Pending', 'tone': 2},
                    {'kind': 10, 'key': 'b3', 'text': 'Error', 'tone': 3},
                    {'kind': 11, 'key': 'a', 'text': 'AB'},
                  ],
                },
                {'kind': 12, 'key': 'd', 'text': 'or'},
              ],
            }),
          ),
        ]),
        section('LIST', [
          const SizedBox(
            height: 130,
            child: _ValueTree({
              'kind': 9,
              'key': 'list',
              'children': [
                {'kind': 0, 'key': 'r0', 'text': 'Q4 pipeline by owner'},
                {'kind': 0, 'key': 'r1', 'text': 'Havbris business card'},
                {'kind': 0, 'key': 'r2', 'text': 'Deals last week'},
              ],
            }),
          ),
        ]),
        section('CHAT', [
          bubble("Pull last week's deals.", user: true),
          bubble('I found 42 deals created or updated last week.', user: false),
        ]),
      ],
    );

    final right = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        section('MENU', [
          SizedBox(
            height: 230,
            child: _ValueTree({
              'kind': 6,
              'key': 'menu',
              'text': menuTrigger,
              'icon': 'more-horizontal',
              'children': [
                {'kind': 0, 'key': 'label', 'text': 'Chat'},
                _button('archive', 'Archive', icon: 'archive'),
                _button('settings', 'Account settings', icon: 'settings'),
                {'kind': 12, 'key': 'sep'},
                _button('signout', 'Sign out', icon: 'log-out'),
              ],
            }),
          ),
        ]),
        section('MODAL', [
          SizedBox(
            height: 330,
            child: _ValueTree({
              'kind': 7,
              'key': 'confirm',
              'text': 'Disconnect Salesforce?',
              'detail': 'Your chats stay.',
              'children': [
                _button('disconnect', 'Disconnect', style: 3),
                _button('cancel', 'Cancel', style: 1),
              ],
            }),
          ),
        ]),
        section('TOAST', [
          SizedBox(
            height: 64,
            child: _ValueTree({
              'kind': 8,
              'key': 'toast',
              'text': 'Chat archived',
              'children': [_button('undo', 'Undo')],
            }),
          ),
        ]),
      ],
    );

    return ColoredBox(
      color: c.page,
      child: Padding(
        padding: const EdgeInsets.all(28),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(child: left),
            const SizedBox(width: 32),
            Expanded(child: middle),
            const SizedBox(width: 32),
            Expanded(child: right),
          ],
        ),
      ),
    );
  }
}

/// A value-node tree drawn with the value-node widgets inside a private
/// [Overlay], so overlay kinds draw within this box.
class _ValueTree extends StatelessWidget {
  const _ValueTree(this.root);
  final Map<String, dynamic> root;

  static VoidCallback? _action(Map node) => () {};

  static Widget _node(Map node) {
    final children = [
      for (final child in (node['children'] as List? ?? const []))
        if (child is Map) child,
    ];
    final text = node['text'] as String? ?? '';
    return switch (node['kind']) {
      0 => Text(text),
      1 => UiMixButton.preset(
        label: text,
        icon: node['icon'] as String?,
        style: UiMixButtonStyle.values[(node['style'] as int? ?? 0)],
        onPressed: () {},
      ),
      2 => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 8,
        children: [for (final child in children) _node(child)],
      ),
      3 => Row(
        spacing: (node['gap'] as num? ?? 0).toDouble(),
        children: [
          for (var i = 0; i < children.length; i++)
            SizedBox(
              width: ((node['extents'] as List?)?[i] as num?)?.toDouble(),
              child: _node(children[i]),
            ),
        ],
      ),
      6 => UiMenu(node: node, action: _action),
      7 => Builder(
        builder: (context) {
          final c = UiColors.of(context);
          final type = UiType.of(context);
          final detail = node['detail'] as String? ?? '';
          return Container(
            color: c.scrim,
            alignment: Alignment.center,
            padding: const EdgeInsets.all(16),
            child: UiMixSurface(
              padding: const EdgeInsets.all(20),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(text, style: type.cardTitle),
                  if (detail.isNotEmpty) ...[
                    const SizedBox(height: 6),
                    Text(detail, style: type.bodyMuted),
                  ],
                  const SizedBox(height: 16),
                  Row(
                    mainAxisAlignment: MainAxisAlignment.end,
                    spacing: 8,
                    children: [for (final child in children) _node(child)],
                  ),
                ],
              ),
            ),
          );
        },
      ),
      8 => UiMixToastBody(
        message: text,
        actionLabel: children.isEmpty
            ? null
            : children.first['text'] as String?,
        onAction: children.isEmpty ? null : () {},
      ),
      9 => Builder(
        builder: (context) {
          final c = UiColors.of(context);
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              for (var i = 0; i < children.length; i++) ...[
                if (i > 0) Container(height: 1, color: c.hairline),
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 8),
                  child: _node(children[i]),
                ),
              ],
            ],
          );
        },
      ),
      10 => UiBadge.fromWire(label: text, tone: node['tone']),
      11 => Builder(
        builder: (context) {
          final c = UiColors.of(context);
          final type = UiType.of(context);
          return Container(
            width: 34,
            height: 34,
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: c.accentTint,
              shape: BoxShape.circle,
            ),
            child: Text(text, style: type.metaStrong.copyWith(color: c.accent)),
          );
        },
      ),
      12 => Builder(
        builder: (context) {
          final c = UiColors.of(context);
          final line = Container(height: 1, color: c.hairline);
          if (text.isEmpty) return line;
          return Row(
            children: [
              Expanded(child: line),
              const SizedBox(width: 14),
              Text(
                text,
                style: UiType.of(context).bodyMuted.copyWith(fontSize: 13),
              ),
              const SizedBox(width: 14),
              Expanded(child: line),
            ],
          );
        },
      ),
      _ => const SizedBox.shrink(),
    };
  }

  @override
  Widget build(BuildContext context) => ClipRect(
    child: Overlay(initialEntries: [OverlayEntry(builder: (_) => _node(root))]),
  );
}
