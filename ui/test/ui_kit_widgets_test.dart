import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('chart title is visible', (tester) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: UiChart(
            part: UiChartPart(
              title: 'E2E BTC chart',
              chartKind: 'line',
              points: [UiChartPoint(label: 'open', value: 64000)],
            ),
          ),
        ),
      ),
    );
    expect(find.text('E2E BTC chart'), findsOneWidget);
  });

  testWidgets('browser shows its title without a raw URL row', (tester) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: UiWebBrowser(
            part: UiWebBrowserPart(
              name: 'e2e',
              title: 'WinUI',
              uri: 'https://learn.microsoft.com/',
            ),
          ),
        ),
      ),
    );
    expect(find.text('WinUI'), findsOneWidget);
    expect(find.text('https://learn.microsoft.com/'), findsNothing);
  });

  testWidgets('expander shows collapsed status', (tester) async {
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: UiExpander(
            part: UiExpanderPart(name: 'e2e', header: 'More', expanded: false),
          ),
        ),
      ),
    );
    expect(find.text('More'), findsOneWidget);
    expect(find.text('Collapsed'), findsOneWidget);
  });
}
