import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'fake_settings_api.dart';
import 'settings_harness.dart';

final class _FailingApi extends FakeSettingsApi {
  @override
  Future<void> putNotifications(Map<String, bool> preferences) =>
      Future.error(StateError('nope'));
}

bool _on(WidgetTester tester, String key) =>
    tester.widget<UiKitSwitch>(find.byKey(Key(key))).value;

void main() {
  testWidgets('toggle_persists_and_rolls_back_on_error', (tester) async {
    final api = FakeSettingsApi();
    await pumpSettings(tester, api, tab: 'notifications');
    expect(find.byType(UiKitSwitch), findsNWidgets(4));
    expect(_on(tester, 'settings-notification-agent-finished'), isTrue);
    expect(_on(tester, 'settings-notification-product-updates'), isFalse);

    await tester.tap(
      find.byKey(const Key('settings-notification-agent-finished')),
    );
    await tester.pumpAndSettle();
    expect(_on(tester, 'settings-notification-agent-finished'), isFalse);
    expect(api.notificationPrefs['agent-finished'], isFalse);
    expect(find.text('Saved'), findsOneWidget);

    await pumpSettings(tester, _FailingApi(), tab: 'notifications');
    await tester.tap(
      find.byKey(const Key('settings-notification-product-updates')),
    );
    await tester.pumpAndSettle();
    expect(_on(tester, 'settings-notification-product-updates'), isFalse);
    expect(find.text('Could not save that change.'), findsOneWidget);
    await tester.pump(kitToastLifetime);
  });
}
