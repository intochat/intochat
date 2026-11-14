import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _view = 'local/apps/change/view';

Map<String, dynamic> _published(Map<String, dynamic> root) => {
  'generation': 2,
  'view': {'title': 'Change Review', 'root': root},
};

Future<void> _host(WidgetTester tester, Map<String, dynamic> root) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.light('intochat'),
      home: Scaffold(
        body: ViewHost(
          knowsApp: (_) => false,
          kind: 'view',
          name: _view,
          load: (kind, name) async => _published(root),
          watch: (kind, name) => const Stream.empty(),
          onAction: (_) async => null,
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
    'diffCard renders repo, target branch, short SHA, +/- stats, unified hunks, and toggles file collapse',
    (tester) async {
      await _host(tester, {
        'kind': 'diffCard',
        'id': 'person-expiry',
        'repo': 'intochat/digitalbrain',
        'targetBranch': 'main',
        'headSha': '8d3e21b9f0123456',
        'filesChanged': 1,
        'additions': 2,
        'deletions': 1,
        'files': [
          {
            'path': 'src/DigitalBrain/Confirmation/ConfirmationState.cs',
            'changeKind': 'modified',
            'additions': 2,
            'deletions': 1,
            'hunks': [
              {
                'header': '@@ -84,3 +84,4 @@',
                'lines': [
                  ' var now = clock.GetUtcNow();',
                  '-var expiry = TimeSpan.FromMinutes(5);',
                  '+var expiry = plan.RequiresPerson',
                  '+    ? TimeSpan.FromDays(7) : TimeSpan.FromMinutes(5);',
                ],
              },
            ],
          },
        ],
      });

      expect(find.text('intochat/digitalbrain'), findsOneWidget);
      expect(find.text('main'), findsOneWidget);
      expect(find.text('8d3e21b9'), findsOneWidget);
      expect(find.text('1 file'), findsOneWidget);
      expect(
        find.text('src/DigitalBrain/Confirmation/ConfirmationState.cs'),
        findsOneWidget,
      );
      expect(find.text('@@ -84,3 +84,4 @@'), findsOneWidget);
      expect(
        find.text('-var expiry = TimeSpan.FromMinutes(5);'),
        findsOneWidget,
      );
      expect(find.text('+var expiry = plan.RequiresPerson'), findsOneWidget);

      // Collapse file hunks locally on click.
      await tester.tap(
        find.byKey(
          const ValueKey(
            'diffFileToggle:src/DigitalBrain/Confirmation/ConfirmationState.cs',
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('@@ -84,3 +84,4 @@'), findsNothing);

      // Re-expand file hunks.
      await tester.tap(
        find.byKey(
          const ValueKey(
            'diffFileToggle:src/DigitalBrain/Confirmation/ConfirmationState.cs',
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('@@ -84,3 +84,4 @@'), findsOneWidget);
    },
  );
}
