import 'package:digitalbrain_flutter_shell/workspace/workspace_desktop.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('the desktop shows only the focused window and close closes it', (
    tester,
  ) async {
    final store = WorkspaceStore(persistence: MemoryWorkspacePersistence());
    store.addArtifact(
      WorkspaceArtifact(id: 'note', title: 'Notes', kind: 'document'),
    );
    store.addArtifact(
      WorkspaceArtifact(id: 'plan', title: 'Plan', kind: 'document'),
    );
    store.openArtifact('note');
    store.openArtifact('plan');
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ListenableBuilder(
            listenable: store,
            builder: (_, _) => WorkspaceDesktop(
              store: store,
              editorBuilder: (artifact) => Text('editor ${artifact.title}'),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.text('editor Plan'), findsOneWidget);
    expect(find.text('editor Notes'), findsNothing);
    expect(find.byKey(const ValueKey('window-drag-plan')), findsNothing);

    store.focusWindow('note');
    await tester.pumpAndSettle();
    expect(find.text('editor Notes'), findsOneWidget);
    expect(find.text('editor Plan'), findsNothing);

    await tester.tap(find.byKey(const ValueKey('window-close-note')));
    await tester.pumpAndSettle();
    expect(find.text('editor Plan'), findsOneWidget);
    expect(store.currentProject.presentation.openArtifactIds, ['plan']);

    await tester.tap(find.byKey(const ValueKey('window-close-plan')));
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('desktop-empty')), findsOneWidget);
    store.dispose();
  });

  testWidgets('an editor keeps its identity while its window stays focused', (
    tester,
  ) async {
    final store = WorkspaceStore(persistence: MemoryWorkspacePersistence());
    store.addArtifact(
      WorkspaceArtifact(id: 'note', title: 'Notes', kind: 'document'),
    );
    store.openArtifact('note');
    var builds = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ListenableBuilder(
            listenable: store,
            builder: (_, _) => WorkspaceDesktop(
              store: store,
              editorBuilder: (artifact) {
                builds++;
                return Text(artifact.title);
              },
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    final before = builds;
    store.save();
    await tester.pumpAndSettle();
    expect(builds, before);
    store.dispose();
  });
}
