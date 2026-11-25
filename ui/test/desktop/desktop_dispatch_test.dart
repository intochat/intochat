import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';

void main() {
  const keys = {'dock-ino', 'focus-w-1'};

  test('a desktop node the view holds sends its command over /act', () {
    expect(
      desktopNodeAct(
        {
          'key': 'dock-ino',
          'command': {
            'target': '.',
            'signal': 'desktop.open',
            'args': {'app': 'ino'},
          },
        },
        keys,
        'b/apps/desktop/view',
      ),
      {
        'kind': 'act',
        'neuron': 'b/apps/desktop',
        'target': '.',
        'signal': 'desktop.open',
        'args': {'app': 'ino'},
      },
    );
  });

  test('a node with an action, a key the view does not hold, no command, or disabled sends nothing', () {
    const command = {'target': '.', 'signal': 'desktop.open'};
    expect(
      desktopNodeAct(
        {'key': 'rogue', 'action': 'widget-open:launcher', 'command': command},
        keys,
        'b/apps/desktop/view',
      ),
      isNull,
    );
    expect(
      desktopNodeAct(
        {'key': 'dock-ino', 'action': 'widget-open:launcher'},
        keys,
        'b/apps/desktop/view',
      ),
      isNull,
    );
    expect(
      desktopNodeAct(
        {'key': 'dock-ino', 'enabled': false, 'command': command},
        keys,
        'b/apps/desktop/view',
      ),
      isNull,
    );
  });

  test('only the shell-owned (app, operation) pairs are sent', () {
    expect(desktopShellOperationAllowed('desktop', 'widget-open'), isTrue);
    expect(desktopShellOperationAllowed('window', 'sandbox-restart'), isTrue);
    expect(desktopShellOperationAllowed('desktop', 'delete-brain'), isFalse);
    expect(desktopShellOperationAllowed('identity', 'open'), isFalse);
  });
}
