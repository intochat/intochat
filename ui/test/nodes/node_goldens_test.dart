@Tags(['golden'])
library;

import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'node_samples.dart';

void main() {
  if (!Platform.isWindows) {
    test(
      'node goldens',
      () {},
      skip: 'Goldens are Windows-rendered; Windows-local regression net only.',
    );
    return;
  }
  final themes = <String, ThemeData Function()>{
    'light': () => UiTheme.light('intochat'),
    'dark': () => UiTheme.dark('intochat'),
  };
  for (final kind in const ['column', 'row', 'card', 'button']) {
    for (final theme in themes.entries) {
      final name = 'node_${kind}_${theme.key}';
      testWidgets(name, (tester) async {
        tester.view.physicalSize = const Size(480, 240);
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        await tester.pumpWidget(
          MaterialApp(
            debugShowCheckedModeBanner: false,
            theme: theme.value(),
            home: Material(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: ViewHost(
                  knowsApp: (_) => false,
                  kind: 'view',
                  name: 'local/apps/sample/view',
                  load: (kind, name) async => PublishedViewPayload.fromJson({
                    'generation': 1,
                    'view': {'title': kind, 'root': nodeSamples[kind]},
                  }),
                  watch: (kind, name) => const Stream.empty(),
                  onAction: (event) async => null,
                ),
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();
        await expectLater(
          find.byType(MaterialApp),
          matchesGoldenFile('goldens/$name.png'),
        );
      });
    }
  }
}
