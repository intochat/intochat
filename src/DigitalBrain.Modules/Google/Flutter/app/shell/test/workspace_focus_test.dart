import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
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
