import 'package:intochat_os_flutter_shell/workspace/settings/settings_api.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'fake_settings_api.dart';
import 'settings_harness.dart';

final class _FailingApi extends FakeSettingsApi {
  @override
  Future<NewTabPreferences> putNewTabApp(String name) =>
      Future.error(StateError('nope'));
}

String? _value(WidgetTester tester) => tester
    .widget<UiKitSelect<String>>(
      find.byKey(const Key('settings-general-new-tab-app')),
    )
    .value;

void main() {
  testWidgets('general_tab_lists_choices_and_saves_new_tab_app', (
    tester,
  ) async {
    final api = FakeSettingsApi();
    await pumpSettings(tester, api);
    expect(find.text('New tab opens'), findsOneWidget);
    expect(_value(tester), 'ino');

    await tester.tap(find.byKey(const Key('settings-general-new-tab-app')));
    await tester.pumpAndSettle();
    expect(find.text('Ino'), findsWidgets);
    await tester.tap(find.text('Notes').last);
    await tester.pumpAndSettle();

    expect(api.newTabPuts, ['notes']);
    expect(_value(tester), 'notes');
    expect(find.text('Saved'), findsOneWidget);
  });

  testWidgets('general_tab_rolls_back_new_tab_app_on_error', (tester) async {
    await pumpSettings(tester, _FailingApi());
    await tester.tap(find.byKey(const Key('settings-general-new-tab-app')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Notes').last);
    await tester.pumpAndSettle();

    expect(_value(tester), 'ino');
    expect(find.text('Could not save that change.'), findsOneWidget);
    await tester.pump(kitToastLifetime);
  });
}
