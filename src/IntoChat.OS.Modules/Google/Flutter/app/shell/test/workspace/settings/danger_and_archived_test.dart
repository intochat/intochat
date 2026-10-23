import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'fake_settings_api.dart';
import 'settings_harness.dart';

void main() {
  testWidgets('archived_empty_state_text', (tester) async {
    await pumpSettings(tester, FakeSettingsApi(), tab: 'archived');
    expect(find.text('Archived chats appear here.'), findsOneWidget);
  });

  testWidgets('delete_account_opens_profile_url', (tester) async {
    final opened = <Uri>[];
    await pumpSettings(
      tester,
      FakeSettingsApi(),
      tab: 'danger',
      kernelBaseUri: Uri.parse('https://os.example.test/some/path'),
      onOpen: (url) async => opened.add(url),
    );
    await tester.tap(find.byKey(const Key('settings-delete-account')));
    await tester.pumpAndSettle();
    expect(opened.single.toString(), 'https://os.example.test/account/profile');
  });

  testWidgets('no_brain_delete_button', (tester) async {
    await pumpSettings(tester, FakeSettingsApi(), tab: 'danger');
    expect(find.byType(UiKitButton), findsOneWidget);
    expect(find.textContaining('Delete brain'), findsNothing);
    expect(find.textContaining('Reset desktop'), findsNothing);
  });
}
