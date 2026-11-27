import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/auth/login_screen.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'application_fixture.dart';

void main() {
  testWidgets('navigation_error_shows_kit_toast', (tester) async {
    final f = await ApplicationFixture.start(tester);
    f.server.failOpen = true;
    await f.appState.activateAction({
      'brainId': 'circle-test',
      'application': 'notes',
    });
    await tester.pumpAndSettle();
    expect(find.byType(SnackBar), findsNothing);
    expect(find.byKey(const ValueKey('ui_kit_toast')), findsOneWidget);
    expect(find.textContaining('Could not navigate'), findsOneWidget);
    await tester.pump(kitToastLifetime);
    await f.dispose();
  });

  testWidgets('login_uses_kit_field', (tester) async {
    await tester.pumpWidget(
      MaterialApp(home: LoginScreen(onSubmit: (_, _) async => null)),
    );
    expect(find.byType(UiKitField), findsNWidgets(2));
    expect(find.byType(UiMixButton), findsWidgets);
    for (final key in ['login-username', 'login-password', 'login-submit']) {
      expect(find.byKey(Key(key)), findsOneWidget);
    }
  });
}
