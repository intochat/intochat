import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';

void main() {
  test('remote focus and Home survive rebuilds and reconnect without local guesses', () async {
    final store = WorkspaceStore();
    await store.load();
    final project = store.currentProject;
    const windows = [
      WorkspaceWindow(
        id: 'first',
        title: 'First',
        kind: 'scene',
        neuronId: 'brain/scenes/first',
        isOpen: true,
      ),
      WorkspaceWindow(
        id: 'second',
        title: 'Second',
        kind: 'text',
        neuronId: 'brain/text/second',
        isOpen: true,
      ),
    ];
    store.reconcileWorkspace(
      project,
      WorkspaceSnapshot(revision: 4, windows: windows, focusedWindow: 'first'),
    );
    expect(store.focusedWindow, 'first');
    expect(project.presentation.openArtifactIds, ['first', 'second']);
    final saved = project.toJson();
    expect(
      saved['artifacts'],
      isEmpty,
      reason: 'Remote roots belong to the workspace neuron',
    );
    expect((saved['presentation'] as Map)['activeArtifactId'], isNull);
    expect((saved['presentation'] as Map)['openArtifactIds'], isEmpty);
    String? requested;
    store.onRemoteFocus = (_, id) => requested = id;
    store.focusWindow('second');
    expect(requested, 'second');
    expect(
      store.focusedWindow,
      'first',
      reason: 'Only the accepted server state changes focus',
    );
    store.reconcileWorkspace(
      project,
      WorkspaceSnapshot(revision: 5, windows: windows),
    );
    expect(
      store.focusedWindow,
      isNull,
      reason: 'Home is explicit even with running windows',
    );
    store.reconcileWorkspace(
      project,
      WorkspaceSnapshot(revision: 4, windows: windows, focusedWindow: 'second'),
    );
    expect(store.focusedWindow, isNull);
    store.dispose();
  });
  test(
    'the focused window is the active open window, else the last visible one',
    () {
      final store = WorkspaceStore(persistence: MemoryWorkspacePersistence());
      expect(store.focusedWindow, isNull);
      store.addArtifact(
        WorkspaceArtifact(
          id: 'b/apps/calculator/window',
          title: 'Calculator',
          kind: 'surface',
        ),
      );
      store.addArtifact(
        WorkspaceArtifact(
          id: 'b/apps/assistant/window',
          title: 'Assistant',
          kind: 'surface',
        ),
      );
      store.openArtifact('b/apps/calculator/window');
      store.openArtifact('b/apps/assistant/window');
      expect(store.focusedWindow, 'b/apps/assistant/window');

      store.focusWindow('b/apps/calculator/window');
      expect(store.focusedWindow, 'b/apps/calculator/window');

      store.minimizeArtifact('b/apps/calculator/window');
      expect(store.focusedWindow, 'b/apps/assistant/window');

      store.closeArtifact('b/apps/assistant/window');
      expect(store.focusedWindow, isNull);
      store.dispose();
    },
  );

  test('a window id names its app', () {
    expect(appOfWindow('brain-1/apps/calculator/window'), 'calculator');
    expect(appOfWindow('account-x/ws/apps/assistant/window'), 'assistant');
    expect(appOfWindow('app-csharp'), isNull);
    expect(appOfWindow('brain-1/apps/calculator/surface'), isNull);
  });
}
