import 'application_fixture.dart';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter_test/flutter_test.dart';

import 'workspace_remote_controller_test.dart' show MemoryPersistence;

void main() {
  testWidgets('aShellWithoutAServerBrainDoesNotInventOne', (tester) async {
    final server = ApplicationServer();
    final store = WorkspaceStore(persistence: MemoryPersistence());
    store.createProject('Stale local project');
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: server,
    )..defaultBrainId = 'owner';

    await tester.pumpWidget(
      WorkspaceApp(store: store, programmingClient: client),
    );
    await tester.pumpAndSettle();

    expect(store.currentProject.id, 'owner');
    expect(store.projects.where((p) => p.id == 'owner'), hasLength(1));
  });
}
