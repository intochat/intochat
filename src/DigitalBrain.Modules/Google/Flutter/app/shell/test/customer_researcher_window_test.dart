import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter_test/flutter_test.dart';

import 'workspace_remote_controller_test.dart' show MemoryPersistence;

void main() {
  test('researcher navigation is projected once from the workspace path', () {
    final store = WorkspaceStore(persistence: MemoryPersistence());
    final key = '${store.currentProject.id}/apps/customer-researcher';
    final state = WorkspaceSnapshot(
      revision: 1,
      path: [store.currentProject.id, key],
      open: [key],
    );
    store.reconcileWorkspace(store.currentProject, state);
    store.reconcileWorkspace(store.currentProject, state);
    expect(store.focusedWindow, key);
    expect(store.currentProject.presentation.openArtifactIds, [key]);
    expect(store.currentProject.artifacts.single.remoteManaged, isTrue);
    store.dispose();
  });
}
