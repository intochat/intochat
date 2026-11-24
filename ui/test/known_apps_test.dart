import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/workspace/known_apps.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  final known = knownAppsFor(const ['tasks', 'files']);
  Map<String, dynamic>? open(String arg) => IntentGate.check(
    IntentNode(intentKind: IntentKind.openWindow, arg: arg),
    knowsApp: known.contains,
  );

  test('an OpenWindow intent opens only an app the shell knows', () {
    expect(
      open('os://window/open?app=tasks')?['uri'],
      'os://window/open?app=tasks',
    );
    expect(open('os://window/open?app=files'), isNotNull);
    expect(open('os://window/open?app=unknown-app'), isNull);
    expect(open('https://example.test/'), isNull);
  });
}
