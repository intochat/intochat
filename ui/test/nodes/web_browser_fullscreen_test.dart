import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets(
    'AppShell(edgeToEdge: true) + WebBrowserNode(chrome: frameless) fills viewport without inner card chrome',
    (tester) async {
      final shell = AppShellNode(
        key: 'photocraft-shell',
        sidebar: null,
        toolbar: null,
        inspector: null,
        transition: '',
        edgeToEdge: true,
        body: WebBrowserNode(
          key: 'photocraft-wasm',
          url: '/artcraft/photocraft/index.html',
          title: 'PhotoCraft WASM Studio',
          height: 920,
          executionMode: 'wasm-ui',
          automationName: 'PhotoCraft WASM Studio',
          chrome: 'frameless',
        ),
        automationName: 'PhotoCraft WASM Studio',
      );

      final ctx = NodeContext(
        view: 'local/app.photocraft/default',
        generation: 1,
        send: (_) async {},
      );

      tester.view.physicalSize = const Size(1200, 800);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(body: NodeRenderer.render(shell, ctx)),
        ),
      );
      await tester.pump();

      final browserFinder = find.byKey(
        const Key('ui_webbrowser_local/app.photocraft/default#photocraft-wasm'),
      );
      expect(browserFinder, findsOneWidget);

      final browserSize = tester.getSize(browserFinder);
      expect(browserSize.width, equals(1200));
      expect(browserSize.height, equals(800));

      // Frameless mode must not render the redundant title/refresh header row.
      expect(find.text('PhotoCraft WASM Studio'), findsNothing);
      expect(find.byIcon(Icons.refresh), findsNothing);
    },
  );
}
