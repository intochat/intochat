import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'first_run_test.dart' show FirstRunClient;
import 'workspace_remote_controller_test.dart' show MemoryPersistence;

void main() {
  testWidgets('narrow workspace keeps keyboard-accessible navigation', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
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
    expect(tester.takeException(), isNull);
    final reached = <String>{};
    for (var step = 0; step < 24; step++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      FocusManager.instance.primaryFocus?.context?.visitAncestorElements((
        element,
      ) {
        final widget = element.widget;
        if (widget is IconButton && widget.tooltip != null) {
          reached.add(widget.tooltip!);
        }
        return true;
      });
    }
    expect(reached, containsAll(['Home', 'Connections']));
    expect(find.byTooltip('Home'), findsOneWidget);
    expect(find.byTooltip('Connections'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    await api.events.close();
    store.dispose();
  });
}
