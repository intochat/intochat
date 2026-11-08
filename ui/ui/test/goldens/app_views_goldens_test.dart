@Tags(['golden'])
library;

import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'app_views_support.dart';

Directory _findRepositoryRoot() {
  for (var dir = Directory.current; ; dir = dir.parent) {
    if (File('${dir.path}/Intochat.OS.slnx').existsSync()) {
      return dir;
    }
    if (dir.parent.path == dir.path) {
      throw StateError(
        'Could not locate Intochat.OS.slnx above ${Directory.current.path}',
      );
    }
  }
}

List<File> _discoverViewFixtures(Directory repoRoot) {
  final srcDir = Directory('${repoRoot.path}/src');
  final files = <File>[];
  for (final entity in srcDir.listSync(recursive: true, followLinks: false)) {
    if (entity is! File) continue;
    final normalized = entity.path.replaceAll(r'\', '/');
    if (normalized.contains('/IntoChat.OS.Modules/Microsoft/')) continue;
    if (RegExp(r'/views/[^/]+/[^/]+\.json$').hasMatch(normalized) ||
        RegExp(r'/views/[^/]+\.json$').hasMatch(normalized)) {
      files.add(entity);
    }
  }
  files.sort((a, b) => a.path.compareTo(b.path));
  return files;
}

String _slugFromFile(File file) {
  final name = file.uri.pathSegments.last;
  return name.endsWith('.json') ? name.substring(0, name.length - 5) : name;
}

void main() {
  if (!Platform.isWindows) {
    test(
      'app views goldens',
      () {},
      skip: 'Goldens are Windows-rendered; Windows-local regression net only.',
    );
    return;
  }

  final repoRoot = _findRepositoryRoot();
  final fixtures = _discoverViewFixtures(repoRoot);
  if (fixtures.isEmpty) {
    test('app views fixtures exist', () {
      fail('No views/*.json fixtures found under ${repoRoot.path}/src');
    });
    return;
  }

  setUpAll(() async {
    final root = Directory.current.path;
    for (final (family, path) in [
      ('Inter', 'assets/fonts/Inter-Regular.ttf'),
      ('InterDisplay', 'assets/fonts/InterDisplay-SemiBold.ttf'),
    ]) {
      final file = File('$root/$path');
      if (!file.existsSync()) continue;
      for (final name in [family, 'packages/intochat_os_ui/$family']) {
        final loader = FontLoader(name)
          ..addFont(file.readAsBytes().then(ByteData.sublistView));
        await loader.load();
      }
    }
  });

  final themes = <String, ThemeData Function()>{
    'light': () => UiTheme.light('intochat'),
    'dark': () => UiTheme.dark('intochat'),
  };
  const size = Size(900, 600);

  for (final file in fixtures) {
    final fixtureMap =
        jsonDecode(file.readAsStringSync()) as Map<String, dynamic>;
    final app = (fixtureMap['appId'] as String? ?? 'app').trim();
    final scenarioSlug = _slugFromFile(file);

    for (final theme in themes.entries) {
      final goldenName = 'app_views_${app}_${scenarioSlug}_${theme.key}';
      testWidgets('app view $goldenName', (tester) async {
        tester.view.physicalSize = size;
        tester.view.devicePixelRatio = 1;
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        debugDisableShadows = false;
        final (:client, :surface) = surfaceClientFromFixture(fixtureMap);
        addTearDown(client.close);
        try {
          await tester.pumpWidget(
            MaterialApp(
              debugShowCheckedModeBanner: false,
              theme: theme.value(),
              home: Material(
                child: Align(
                  alignment: Alignment.topLeft,
                  child: SizedBox(
                    width: size.width,
                    height: size.height,
                    child: ApplicationSurface(
                      knowsApp: (_) => false,
                      client: client,
                      workspace: 'local',
                      application: app,
                      start: () async => surface,
                    ),
                  ),
                ),
              ),
            ),
          );
          await tester.pumpAndSettle();
          expect(tester.takeException(), isNull);
          await expectLater(
            find.byType(ApplicationSurface),
            matchesGoldenFile('$goldenName.png'),
          );
          await tester.pumpWidget(const SizedBox());
          await tester.pump();
        } finally {
          debugDisableShadows = true;
        }
      });
    }
  }
}
