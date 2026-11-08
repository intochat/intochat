import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';

void main() {
  const keys = {'dock-ino', 'focus-w-1'};

  test('a desktop node activates only by a key the view holds', () {
    expect(
      desktopViewEvent({'key': 'dock-ino'}, keys, 'b/apps/desktop/view', 3),
      {
        'kind': 'view',
        'name': 'b/apps/desktop/view',
        'generation': 3,
        'key': 'dock-ino',
      },
    );
  });

  test(
    'a node with an action and a key the view does not hold sends nothing',
    () {
      expect(
        desktopViewEvent(
          {'key': 'rogue', 'action': 'widget-open:launcher', 'app': 'desktop'},
          keys,
          'b/apps/desktop/view',
          3,
        ),
        isNull,
      );
      expect(
        desktopViewEvent(
          {'key': 'dock-ino', 'enabled': false},
          keys,
          'b/apps/desktop/view',
          3,
        ),
        isNull,
      );
    },
  );

  test('only the shell-owned (app, operation) pairs are sent', () {
    expect(desktopShellOperationAllowed('desktop', 'widget-open'), isTrue);
    expect(desktopShellOperationAllowed('window', 'sandbox-restart'), isTrue);
    expect(desktopShellOperationAllowed('desktop', 'delete-brain'), isFalse);
    expect(desktopShellOperationAllowed('identity', 'open'), isFalse);
  });
}
