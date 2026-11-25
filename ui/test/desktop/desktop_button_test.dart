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

    expect(desktopButton(node)['argument'], 'w1');
  });
}
