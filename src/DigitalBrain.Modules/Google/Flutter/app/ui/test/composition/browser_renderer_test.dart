import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:digitalbrain_ui/src/components/browser/embedded_browser_windows.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('browser composition is explicit on unsupported platforms', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'webbrowser',
            name: 'workspace/app/browser',
            load: (_, _) async => {'title': 'Customer Researcher', 'uri': ''},
            onAction: (_) async =>
                fail('Unsupported browser must not send an attachment'),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byType(UiWebBrowser), findsOneWidget);
    expect(
      find.text('Live research requires the Windows desktop app.'),
      findsOneWidget,
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets(
    'reconnect replaces the browser session while ordinary rebuilds preserve it',
    (tester) async {
      Future<void> show(String title) async {
        await tester.pumpWidget(
          MaterialApp(
            home: Scaffold(
              body: SizedBox(
                width: 360,
                child: NeuronView(
                  kind: 'webbrowser',
                  name: 'workspace/app/browser',
                  load: (_, _) async => {'title': title, 'uri': ''},
                  onAction: (_) async {},
                ),
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();
      }

      await show('Browser');
      final initial = tester.state(find.byType(EmbeddedBrowser));
      await show('Updated browser');
      expect(tester.state(find.byType(EmbeddedBrowser)), same(initial));
      await tester.tap(find.byTooltip('Reconnect browser'));
      await tester.pumpAndSettle();
      expect(initial.mounted, isFalse);
      expect(tester.state(find.byType(EmbeddedBrowser)), isNot(same(initial)));
      expect(find.byType(UiWebBrowser), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );
}
