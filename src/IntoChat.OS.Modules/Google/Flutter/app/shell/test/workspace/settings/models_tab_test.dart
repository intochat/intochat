import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/workspace/settings/keys_tab.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'fake_settings_api.dart';
import 'settings_harness.dart';

void main() {
  testWidgets('unavailable_model_links_to_keys_tab', (tester) async {
    await pumpSettings(tester, FakeSettingsApi(), tab: 'models');
    expect(dimmed(tester, 'claude-haiku-4-5'), isTrue);
    final link = find.byKey(const Key('model-add-key-preset:IHaiku45'));
    await tester.ensureVisible(link);
    await tester.pumpAndSettle();
    await tester.tap(link);
    await tester.pumpAndSettle();
    expect(find.byType(KeysTab), findsOneWidget);
    final tabs = tester.widget<UiKitTabs>(find.byType(UiKitTabs));
    expect(tabs.labels[tabs.index], 'API keys');
  });

  testWidgets('default_model_select_saves_choice', (tester) async {
    final store = await pumpSettings(tester, FakeSettingsApi(), tab: 'agents');
    expect(find.byType(UiKitSelect<String?>), findsOneWidget);
    await tester.tap(find.byType(UiKitSelect<String?>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('gpt-5.4-nano').last);
    await tester.pumpAndSettle();
    expect(store.settings.defaultModelProfile, 'preset:IGpt54Nano');
  });

  testWidgets('tabs_in_order_and_old_names_map', (tester) async {
    await pumpSettings(tester, FakeSettingsApi(), tab: 'developer');
    final tabs = tester.widget<UiKitTabs>(find.byType(UiKitTabs));
    expect(tabs.labels, [
      'General',
      'AI models',
      'API keys',
      'Connections',
      'Variables and secrets',
      'Notifications',
      'Archived chats',
      'Danger zone',
      'Billing (Later)',
    ]);
    expect(tabs.index, 0);
    expect(tabs.disabled, {8});
    expect(find.byKey(const Key('theme_picker')), findsOneWidget);
  });
}
