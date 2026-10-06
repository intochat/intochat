import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';

import 'workspace_remote_controller_test.dart' show MemoryPersistence;

void main() {
  test('retired local apps cannot be launched', () {
    final store = WorkspaceStore(persistence: MemoryPersistence());
    for (final app in ['files', 'images']) {
      expect(() => store.launchLocalApp(app), throwsArgumentError);
    }
    store.dispose();
  });
  test('app launch minimizes others and preserves a restored size', () {
    final store = WorkspaceStore(
      persistence: MemoryPersistence(),
      seedProject: false,
    )..createProject('Personal');
    store.developerMode = true;
    store.launchLocalApp('csharp');
    expect(store.currentProject.presentation.modeOf('app-csharp'), 'maximized');
    store.restoreWindow('app-csharp');
    store.addArtifact(
      WorkspaceArtifact(id: 'other', title: 'Other', kind: 'surface'),
    );
    store.openArtifact('other');
    store.launchLocalApp('csharp');
    expect(
      store.currentProject.presentation.minimizedArtifactIds,
      contains('other'),
    );
    expect(store.currentProject.presentation.modeOf('app-csharp'), 'floating');
    expect(
      store.currentProject.artifacts.where((a) => a.id == 'app-csharp'),
      hasLength(1),
    );
  });
  testWidgets(
    'empty installation starts in a workspace with floating controls',
    (tester) async {
      tester.view.physicalSize = const Size(1440, 900);
      tester.view.devicePixelRatio = 1;
      final store = WorkspaceStore(
        persistence: MemoryPersistence(),
        seedProject: false,
      );
      await tester.pumpWidget(WorkspaceApp(store: store));
      await tester.pumpAndSettle();
      expect(find.text('Your projects'), findsNothing);
      expect(find.byTooltip('Home'), findsOneWidget);
      expect(find.byTooltip('Compute'), findsOneWidget);
      expect(find.text('Compute'), findsNothing);
      expect(store.currentProject.title, 'Personal');
      await tester.pumpWidget(const SizedBox.shrink());
      tester.view.resetPhysicalSize();
      tester.view.resetDevicePixelRatio();
    },
  );
}
