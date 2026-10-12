import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:digitalbrain_flutter_shell/workspace/dock/assistant_island.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'first_run_test.dart' show FirstRunClient;
import 'workspace_remote_controller_test.dart' show MemoryPersistence;

void main() {
  testWidgets('expanded companion is the only Assistant composer', (
    tester,
  ) async {
    final store = WorkspaceStore(persistence: MemoryPersistence());
    final api = FirstRunClient();
    await tester.pumpWidget(
      WorkspaceApp(
        store: store,
        programmingClient: DigitalBrainUiClient(
          baseUri: Uri.parse('http://test'),
          httpClient: api,
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byType(DockTextField), findsNothing);
    await tester.tap(find.byTooltip('Collapse Assistant'));
    await tester.pumpAndSettle();
    expect(find.byType(DockTextField), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    await api.events.close();
    store.dispose();
  });
}
