import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

void main() {
  test('a button command carries the desktop argument', () {
    final node = Node.fromJson({
      'kind': 'button',
      'key': 'focus-w1',
      'label': 'Notes',
      'command': {'target': '.', 'signal': 'focus', 'args': 'w1'},
    }) as ButtonNode;

    expect(desktopButton(node, const {})['argument'], 'w1');
  });

  test('a stored view with a legacy emit still carries the argument', () {
    final view = {
      'title': 'Desktop',
      'root': {
        'kind': 'row',
        'children': [
          {
            'kind': 'button',
            'key': 'focus-w1',
            'label': 'Notes',
            'emit': {'owner': 'desktop', 'signal': 'focus', 'args': 'w1'},
          },
        ],
      },
    };
    final node = Node.fromJson(
      ((view['root'] as Map)['children'] as List).first as Map<String, dynamic>,
    ) as ButtonNode;

    expect(node.command, isNull);
    expect(desktopButton(node, legacyEmitArgs(view))['argument'], 'w1');
  });
}
