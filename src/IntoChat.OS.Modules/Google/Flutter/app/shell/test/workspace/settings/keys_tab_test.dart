import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/workspace/settings/keys_tab.dart';
import 'package:intochat_os_flutter_shell/workspace/settings/models_tab.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'fake_settings_api.dart';
import 'settings_harness.dart';

Future<void> addKey(WidgetTester tester, String provider, String value) async {
  await tester.tap(find.byKey(Key('settings-key-add-$provider')));
  await tester.pumpAndSettle();
  await tester.enterText(find.byKey(const Key('settings-key-field')), value);
  await tester.tap(find.byKey(const Key('settings-key-save')));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('saving_key_refreshes_models_and_marks_available', (
    tester,
  ) async {
    final api = FakeSettingsApi();
    await pumpSettings(tester, api, tab: 'models');
    expect(dimmed(tester, 'claude-haiku-4-5'), isTrue);
    final loads = api.modelLoads;

    await tester.tap(find.text('API keys'));
    await tester.pumpAndSettle();
    expect(find.byType(KeysTab), findsOneWidget);
    await addKey(tester, 'anthropic', 'sk-ant-0123456789');

    expect(api.puts.single, (
      'provider:anthropic',
      EntryKind.secret,
      'sk-ant-0123456789',
    ));
    await tester.tap(find.text('AI models'));
    await tester.pumpAndSettle();
    expect(api.modelLoads, greaterThan(loads));
    expect(find.byType(ModelsTab), findsOneWidget);
    expect(dimmed(tester, 'claude-haiku-4-5'), isFalse);
  });

  testWidgets('key_field_cleared_after_save', (tester) async {
    final api = FakeSettingsApi();
    await pumpSettings(tester, api, tab: 'keys');
    await tester.tap(find.byKey(const Key('settings-key-add-openai')));
    await tester.pumpAndSettle();
    final field = tester.widget<UiKitField>(find.byType(UiKitField));
    expect(field.secret, isTrue);
    await tester.enterText(
      find.byKey(const Key('settings-key-field')),
      'sk-openai-secret-9876',
    );
    await tester.tap(find.byKey(const Key('settings-key-save')));
    await tester.pumpAndSettle();
    expect(field.controller!.text, isEmpty);
    expect(find.byKey(const Key('settings-key-field')), findsNothing);
    expect(find.textContaining('sk-openai-secret'), findsNothing);
    expect(find.text('Set ••••9876'), findsOneWidget);
  });

  testWidgets('masked_tail_shown', (tester) async {
    final api = FakeSettingsApi(
      secrets: {'provider:openai': 'sk-long-key-WXYZ', 'provider:xai': 'short'},
    );
    await pumpSettings(tester, api, tab: 'keys');
    expect(find.text('Set ••••WXYZ'), findsOneWidget);
    // No tail from the server: set, but nothing of the value is shown.
    expect(find.text('Set'), findsOneWidget);
    expect(find.textContaining('hort'), findsNothing);
    expect(find.text('Not set'), findsNWidgets(3));
    expect(find.text('Replace'), findsNWidgets(2));
  });

  testWidgets('remove_key_confirms_then_unsets', (tester) async {
    final api = FakeSettingsApi(
      secrets: {'provider:openai': 'sk-long-key-WXYZ'},
    );
    await pumpSettings(tester, api, tab: 'keys');
    final loads = api.modelLoads;
    await tester.tap(find.byKey(const Key('settings-key-remove-openai')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('settings-key-remove-cancel')));
    await tester.pumpAndSettle();
    expect(api.removed, isEmpty);
    expect(find.text('Set ••••WXYZ'), findsOneWidget);

    await tester.tap(find.byKey(const Key('settings-key-remove-openai')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('settings-key-remove-confirm')));
    await tester.pumpAndSettle();
    expect(api.removed, ['provider:openai']);
    expect(api.modelLoads, greaterThan(loads));
    expect(find.text('Not set'), findsNWidgets(5));
  });

  testWidgets('non_owner_sees_clear_message', (tester) async {
    await pumpSettings(tester, _ForbiddenApi(), tab: 'keys');
    await addKey(tester, 'openai', 'sk-openai-secret-9876');
    expect(
      find.text('Only the brain owner can change API keys.'),
      findsOneWidget,
    );
  });
}

final class _ForbiddenApi extends FakeSettingsApi {
  @override
  Future<void> putEntry(String name, EntryKind kind, String value) async =>
      throw const SettingsForbidden();
}
