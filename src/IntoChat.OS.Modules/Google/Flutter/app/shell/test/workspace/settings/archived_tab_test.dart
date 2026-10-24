import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/workspace/settings/settings_api.dart';

import 'fake_settings_api.dart';
import 'settings_harness.dart';

FakeSettingsApi _api() => FakeSettingsApi()
  ..archivedList.addAll([
    ArchivedChat(
      id: 'main',
      title: '<b>Plan</b> **launch**',
      updatedAt: DateTime.now().subtract(const Duration(days: 2)),
    ),
    ArchivedChat(
      id: 'c-${'a' * 32}',
      title: 'Second',
      updatedAt: DateTime.now().subtract(const Duration(minutes: 5)),
    ),
  ]);

void main() {
  testWidgets('archived_tab_lists_and_unarchives', (tester) async {
    final api = _api();
    await pumpSettings(tester, api, tab: 'archived');
    await tester.pumpAndSettle();

    expect(find.text('<b>Plan</b> **launch**'), findsOneWidget);
    expect(find.text('2 d ago'), findsOneWidget);

    await tester.tap(find.byKey(const Key('archived-unarchive-main')));
    await tester.pump();
    await tester.pump();

    expect(api.unarchived, ['main']);
    expect(find.text('<b>Plan</b> **launch**'), findsNothing);
    expect(find.text('Second'), findsOneWidget);
    expect(find.text('Chat restored.'), findsOneWidget);
    await tester.pumpAndSettle(const Duration(seconds: 10));
  });

  testWidgets('archived_tab_keeps_row_on_error', (tester) async {
    final api = _api()..unarchiveError = StateError('boom');
    await pumpSettings(tester, api, tab: 'archived');
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('archived-unarchive-main')));
    await tester.pump();
    await tester.pump();

    expect(find.text('<b>Plan</b> **launch**'), findsOneWidget);
    expect(find.text('Chat restored.'), findsNothing);
    expect(find.textContaining('could not be restored'), findsOneWidget);
    expect(find.textContaining('boom'), findsNothing);
    await tester.pumpAndSettle(const Duration(seconds: 10));
  });

  testWidgets('archived_tab_empty_state', (tester) async {
    await pumpSettings(tester, FakeSettingsApi(), tab: 'archived');
    await tester.pumpAndSettle();
    expect(find.text('Archived chats appear here.'), findsOneWidget);
  });

  testWidgets('archived_tab_removes_stale_row_on_404', (tester) async {
    final api = _api()..unarchiveError = const ChatGone();
    await pumpSettings(tester, api, tab: 'archived');
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('archived-unarchive-main')));
    await tester.pump();
    await tester.pump();

    expect(find.byKey(const Key('archived-row-main')), findsNothing);
    expect(find.text('Second'), findsOneWidget);
    await tester.pumpAndSettle(const Duration(seconds: 10));
  });

  testWidgets('archived_tab_shows_more_pages', (tester) async {
    final api = _api()..pageSize = 1;
    await pumpSettings(tester, api, tab: 'archived');
    await tester.pumpAndSettle();
    expect(find.text('Second'), findsNothing);

    await tester.tap(find.byKey(const Key('archived-show-more')));
    await tester.pumpAndSettle();

    expect(api.archivedSkips, [0, 1]);
    expect(find.text('Second'), findsOneWidget);
    expect(find.byKey(const Key('archived-show-more')), findsNothing);
  });
}
