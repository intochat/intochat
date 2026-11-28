@Tags(['golden'])
library;

import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

// A copy button: the server answers its activation with a Copy intent the client performs.
Widget _copy() => ViewHost(
  kind: 'view',
  name: 'local/apps/share/view',
  overrides: ViewHostOverrides(
    knowsApp: (_) => false,
    load: (kind, name) async => PublishedViewPayload.fromJson({
    'generation': 1,
    'view': {
      'title': 'Share',
      'root': {
        'kind': 'column',
        'children': [
          {
            'kind': 'button',
            'key': 'copy',
            'label': 'Copy link',
            'intent': {
              'intentKind': 'Copy',
              'arg': 'https://example.test/#/open?app=fixture-app',
            },
            'icon': 'copy',
          },
        ],
      },
    },
  }),
    watch: (kind, name) => const Stream.empty(),
    onAction: (event) async => {
    'intent': {
      'kind': 'Copy',
      'arg': 'https://example.test/#/open?app=fixture-app',
    },
  },
  ),
);

void main() {
  if (!Platform.isWindows) {
    test(
      'kit control goldens',
      () {},
      skip: 'Goldens are Windows-rendered; Windows-local regression net only.',
    );
    return;
  }
  final themes = <String, ThemeData Function()>{
    'light': () => UiTheme.light('intochat'),
    'dark': () => UiTheme.dark('intochat'),
  };
  final sizes = <String, Size>{
    'wide': const Size(1440, 900),
    'narrow': const Size(390, 844),
  };
  final scenes = <String, Widget Function()>{'copy_toast': _copy};
  for (final theme in themes.entries) {
    for (final size in sizes.entries) {
      for (final scene in scenes.entries) {
        final name = '${scene.key}_${size.key}_${theme.key}';
        testWidgets('kit control $name', (tester) async {
          tester.view.physicalSize = size.value;
          tester.view.devicePixelRatio = 1;
          addTearDown(tester.view.resetPhysicalSize);
          addTearDown(tester.view.resetDevicePixelRatio);
          tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
            SystemChannels.platform,
            (call) async => null,
          );
          addTearDown(
            () => tester.binding.defaultBinaryMessenger
                .setMockMethodCallHandler(SystemChannels.platform, null),
          );
          debugDisableShadows = false;
          try {
            await tester.pumpWidget(
              MaterialApp(
                debugShowCheckedModeBanner: false,
                theme: theme.value(),
                home: Scaffold(
                  body: Align(
                    alignment: Alignment.topLeft,
                    child: Padding(
                      padding: const EdgeInsets.all(24),
                      child: SizedBox(
                        width: size.value.width - 48,
                        child: scene.value(),
                      ),
                    ),
                  ),
                ),
              ),
            );
            await tester.pumpAndSettle();
            if (scene.key == 'copy_toast') {
              await tester.tap(find.widgetWithText(UiMixButton, 'Copy link'));
              await tester.pump();
              await tester.pump(const Duration(milliseconds: 400));
            }
            expect(tester.takeException(), isNull);
            await expectLater(
              find.byType(MaterialApp),
              matchesGoldenFile('kit_controls_$name.png'),
            );
            if (scene.key == 'copy_toast') {
              await tester.pump(const Duration(seconds: 10));
              await tester.pumpAndSettle();
            }
          } finally {
            debugDisableShadows = true;
          }
        });
      }
    }
  }
}
