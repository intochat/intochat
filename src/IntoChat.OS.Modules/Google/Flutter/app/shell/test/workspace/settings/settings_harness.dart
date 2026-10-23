import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/workspace/settings/settings_api.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_settings.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_store.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

Future<WorkspaceStore> pumpSettings(
  WidgetTester tester,
  SettingsApi api, {
  String tab = 'general',
}) async {
  final store = WorkspaceStore();
  addTearDown(store.dispose);
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: WorkspaceSettings(
        brainId: 'brain-1',
        store: store,
        initialTab: tab,
        onClose: () {},
        api: api,
      ),
    ),
  );
  await tester.pumpAndSettle();
  return store;
}

/// Whether the model row labelled [label] is dimmed as unavailable.
bool dimmed(WidgetTester tester, String label) => find
    .ancestor(of: find.text(label).last, matching: find.byType(Opacity))
    .evaluate()
    .any((e) => (e.widget as Opacity).opacity < 1);
