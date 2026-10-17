import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('a friend profile renders and revoked content is cleared', (
    tester,
  ) async {
    final changes = StreamController<void>.broadcast();
    var removed = false;
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'person',
            name: 'bob/profile',
            load: (_, _) async {
              if (removed) {
                throw UiRequestException(
                  'GET',
                  '/apps/node',
                  403,
                  'No shared circle',
                );
              }
              return {'version': 1, 'displayName': 'Bob', 'avatarUrl': ''};
            },
            watch: (_, _) => changes.stream,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Bob'), findsOneWidget);
    removed = true;
    changes.add(null);
    await tester.pumpAndSettle();
    expect(find.text('Bob'), findsNothing);
    await tester.pumpWidget(const SizedBox());
    await changes.close();
  });
}
