import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  paletteTests();
  testWidgets('a select option activates its key on the view', (tester) async {
    final events = <Map<String, dynamic>>[];
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: 'brain-1/apps/settings/view',
            load: (_, _) async => {
              'generation': 4,
              'view': {
                'title': 'Settings',
                'root': {
                  'kind': 'select',
                  'label': 'Model',
                  'selected': '',
                  'options': [
                    {
                      'key': 'model-fast',
                      'id': 'profile:fast',
                      'label': 'Fast',
                      'emit': {'owner': 'apps/settings', 'signal': 'model'},
                    },
                  ],
                },
              },
            },
            watch: (_, _) => const Stream.empty(),
            onAction: (event) async => events.add(event),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byType(UiKitSelect<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Fast'));
    await tester.pumpAndSettle();
    expect(events.single, {
      'kind': 'view',
      'name': 'brain-1/apps/settings/view',
      'generation': 4,
      'key': 'model-fast',
    });
    await tester.pumpWidget(const SizedBox());
  });
}

void paletteTests() {
  test('old theme values load as palette names and palettes round-trip', () {
    expect(WorkspacePreferences.fromJson({'theme': 'dark'}).theme, 'midnight');
    expect(WorkspacePreferences.fromJson({'theme': 'light'}).theme, 'paper');
    expect(WorkspacePreferences.fromJson({}).theme, 'intochat');
    expect(
      WorkspacePreferences.fromJson({'theme': 'system'}).theme,
      'intochat',
    );
    expect(
      WorkspacePreferences.fromJson({'theme': 'midnight'}).theme,
      'midnight',
    );
    expect(WorkspacePreferences.fromJson({'theme': 'lumen'}).theme, 'lumen');
    expect(WorkspacePreferences().toJson()['theme'], 'intochat');
  });
}
