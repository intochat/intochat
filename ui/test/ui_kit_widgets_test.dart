import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
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
}
