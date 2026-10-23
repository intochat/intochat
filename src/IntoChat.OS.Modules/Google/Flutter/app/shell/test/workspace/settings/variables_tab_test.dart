import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/workspace/settings/settings_api.dart';

import 'fake_settings_api.dart';
import 'settings_harness.dart';

void main() {
  testWidgets('variable_value_visible_secret_masked', (tester) async {
    final api = FakeSettingsApi(
      variables: {'REGION': 'westeurope'},
      secrets: {'DB_PASSWORD': 'fake-value'},
    );
    await pumpSettings(tester, api, tab: 'variables');
    expect(find.text('REGION'), findsOneWidget);
    expect(find.text('westeurope'), findsOneWidget);
    expect(find.text('••••alue'), findsOneWidget);
    expect(find.textContaining('fake-value'), findsNothing);
  });

  testWidgets('provider_entries_hidden_from_variables', (tester) async {
    final api = FakeSettingsApi(
      variables: {'REGION': 'x'},
      secrets: {'provider:openai': 'sk-openai-0123456789'},
    );
    await pumpSettings(tester, api, tab: 'variables');
    expect(find.text('REGION'), findsOneWidget);
    expect(find.textContaining('provider:'), findsNothing);
    expect(find.text('••••6789'), findsNothing);
  });

  testWidgets('invalid_name_shows_message_and_valid_variable_saves', (
    tester,
  ) async {
    final api = FakeSettingsApi();
    await pumpSettings(tester, api, tab: 'variables');
    await tester.tap(find.byKey(const Key('settings-variable-add')));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('settings-variable-name')),
      'bad name',
    );
    await tester.enterText(
      find.byKey(const Key('settings-variable-value')),
      'v',
    );
    await tester.tap(find.byKey(const Key('settings-variable-save')));
    await tester.pumpAndSettle();
    expect(find.text('Use letters, digits, _ . - only'), findsOneWidget);
    expect(api.puts, isEmpty);

    await tester.enterText(
      find.byKey(const Key('settings-variable-name')),
      'MY_VAR.1-a',
    );
    await tester.tap(find.byKey(const Key('settings-variable-save')));
    await tester.pumpAndSettle();
    expect(api.puts.single, ('MY_VAR.1-a', EntryKind.variable, 'v'));
    expect(find.text('MY_VAR.1-a'), findsOneWidget);
  });

  testWidgets('secret_saved_masked_and_value_not_kept', (tester) async {
    final api = FakeSettingsApi();
    await pumpSettings(tester, api, tab: 'variables');
    await tester.tap(find.byKey(const Key('settings-variable-add')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Variable').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Secret').last);
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('settings-variable-name')),
      'TOKEN',
    );
    await tester.enterText(
      find.byKey(const Key('settings-variable-value')),
      'tok-0123456789',
    );
    await tester.tap(find.byKey(const Key('settings-variable-save')));
    await tester.pumpAndSettle();
    expect(api.puts.single.$2, EntryKind.secret);
    expect(find.text('••••6789'), findsOneWidget);
    expect(find.textContaining('tok-0123'), findsNothing);
    expect(tester.widgetList<EditableText>(find.byType(EditableText)), isEmpty);
  });

  testWidgets('remove_only_after_confirming', (tester) async {
    final api = FakeSettingsApi(variables: {'REGION': 'x'});
    await pumpSettings(tester, api, tab: 'variables');
    await tester.tap(find.byKey(const Key('settings-variable-remove-REGION')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('settings-variable-remove-cancel')));
    await tester.pumpAndSettle();
    expect(api.removed, isEmpty);
    expect(find.text('REGION'), findsOneWidget);

    await tester.tap(find.byKey(const Key('settings-variable-remove-REGION')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('settings-variable-remove-confirm')));
    await tester.pumpAndSettle();
    expect(api.removed, ['REGION']);
    expect(find.text('REGION'), findsNothing);
  });

  testWidgets('barrier_tap_mid_save_keeps_dialog_and_reloads_list', (
    tester,
  ) async {
    final gate = Completer<void>();
    final api = _SlowApi(gate.future);
    await pumpSettings(tester, api, tab: 'variables');
    await tester.tap(find.byKey(const Key('settings-variable-add')));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('settings-variable-name')),
      'A',
    );
    await tester.enterText(
      find.byKey(const Key('settings-variable-value')),
      'v',
    );
    await tester.tap(find.byKey(const Key('settings-variable-save')));
    await tester.pump();
    await tester.tapAt(const Offset(2, 2));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('settings-variable-save')), findsOneWidget);
    gate.complete();
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('settings-variable-save')), findsNothing);
    expect(find.text('A'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}

final class _SlowApi extends FakeSettingsApi {
  _SlowApi(this.gate);
  final Future<void> gate;
  @override
  Future<void> putEntry(String name, EntryKind kind, String value) async {
    await gate;
    await super.putEntry(name, kind, value);
  }
}
