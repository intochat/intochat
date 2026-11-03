import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'application_fixture.dart';

void main() {
  testWidgets('Ino opens its typed surface and requests target Ino', (
    tester,
  ) async {
    final f = await ApplicationFixture.start(tester);
    expect(f.renderedSurface, '${f.brainId}/apps/ino/surface');
    expect(find.widgetWithText(TextField, 'Message'), findsWidgets);
    await f.appState.activateAction({
      'kind': 'workspace',
      'application': 'ino',
      'operation': 'send',
      'input': 'hello',
    });
    await tester.pumpAndSettle();
    final request = f.server
        .requestsTo('/brains/${f.brainId}/apps/ino/request')
        .single;
    expect(request.body, {'operation': 'send', 'input': 'hello'});
    await f.dispose();
  });
}
