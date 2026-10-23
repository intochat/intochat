@Tags(['golden'])
library;

import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_settings.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_store.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import '../workspace/settings/fake_settings_api.dart';

Future<void> _font(String family, String path) async {
  final file = File(path);
  if (!file.existsSync()) return;
  final loader = FontLoader(family)
    ..addFont(file.readAsBytes().then(ByteData.sublistView));
  await loader.load();
}

String? _packageRoot(String package) {
  for (var dir = Directory.current; ; dir = dir.parent) {
    final file = File('${dir.path}/.dart_tool/package_config.json');
    if (file.existsSync()) {
      final packages = (jsonDecode(file.readAsStringSync()) as Map)['packages'];
      for (final entry in (packages as List).cast<Map>()) {
        if (entry['name'] == package) {
          return file.parent.uri
              .resolve(entry['rootUri'] as String)
              .toFilePath();
        }
      }
    }
    if (dir.parent.path == dir.path) return null;
  }
}

Future<void> _loadFonts() async {
  final ui = '${Directory.current.path}/../ui/assets/fonts';
  for (final (family, name) in [
    ('Schibsted Grotesk', 'SchibstedGrotesk-Variable.ttf'),
    ('JetBrains Mono', 'JetBrainsMono-Variable.ttf'),
  ]) {
    await _font(family, '$ui/$name');
    await _font('packages/intochat_os_ui/$family', '$ui/$name');
  }
  final flutterRoot = Platform.environment['FLUTTER_ROOT'];
  if (flutterRoot != null) {
    await _font(
      'MaterialIcons',
      '$flutterRoot/bin/cache/artifacts/material_fonts/MaterialIcons-Regular.otf',
    );
  }
  final lucide = _packageRoot('lucide_icons_flutter');
  if (lucide != null) {
    await _font(
      'packages/lucide_icons_flutter/Lucide',
      '$lucide/assets/lucide.ttf',
    );
  }
}

void main() {
  if (!Platform.isWindows) {
    test(
      'settings goldens',
      () {},
      skip: 'Goldens are Windows-rendered; Windows-local regression net only.',
    );
    return;
  }
  setUpAll(_loadFonts);
  const tabs = [
    'general',
    'models',
    'keys',
    'connections',
    'variables',
    'notifications',
    'archived',
    'danger',
  ];
  const sizes = {'desktop': Size(1440, 900), 'mobile': Size(390, 844)};
  for (final tab in tabs) {
    for (final size in sizes.entries) {
      for (final dark in [false, true]) {
        final mode = dark ? 'dark' : 'light';
        testWidgets('settings $tab ${size.key} $mode', (tester) async {
          tester.view.physicalSize = size.value;
          tester.view.devicePixelRatio = 1;
          addTearDown(tester.view.resetPhysicalSize);
          addTearDown(tester.view.resetDevicePixelRatio);
          debugDisableShadows = false;
          try {
            final store = WorkspaceStore();
            addTearDown(store.dispose);
            await tester.pumpWidget(
              MaterialApp(
                debugShowCheckedModeBanner: false,
                theme: dark
                    ? UiTheme.intochat(Brightness.dark)
                    : UiTheme.intochat(Brightness.light),
                home: WorkspaceSettings(
                  brainId: 'brain-1',
                  store: store,
                  initialTab: tab,
                  onClose: () {},
                  api: FakeSettingsApi(
                    secrets: {'provider:openai': 'sk-openai-1234567890'},
                    variables: {'REGION': 'eu-west'},
                  ),
                ),
              ),
            );
            await tester.pumpAndSettle();
            // A RenderFlex overflow surfaces as a framework exception.
            expect(tester.takeException(), isNull);
            await expectLater(
              find.byType(WorkspaceSettings),
              matchesGoldenFile('settings_${tab}_${size.key}_$mode.png'),
            );
          } finally {
            debugDisableShadows = true;
          }
        });
      }
    }
  }
}
