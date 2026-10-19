import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

/// Loads the bundled OFL fonts so goldens render real glyphs instead of Ahem.
/// Registered under both the bare family and the package-qualified family
/// (`UiType.sans` / `UiType.mono`). v1 families (Segoe UI, Cascadia Mono)
/// are not loaded and fall back to the test font.
Future<void> testExecutable(FutureOr<void> Function() testMain) async {
  TestWidgetsFlutterBinding.ensureInitialized();
  Future<void> load(String family, String path) async {
    final file = File(path);
    if (!file.existsSync()) return;
    final loader = FontLoader(family)
      ..addFont(file.readAsBytes().then(ByteData.sublistView));
    await loader.load();
  }

  for (final (family, path) in [
    ('Schibsted Grotesk', 'assets/fonts/SchibstedGrotesk-Variable.ttf'),
    ('JetBrains Mono', 'assets/fonts/JetBrainsMono-Variable.ttf'),
  ]) {
    await load(family, path);
    await load('packages/intochat_os_ui/$family', path);
  }
  final flutterRoot = Platform.environment['FLUTTER_ROOT'];
  if (flutterRoot != null) {
    await load(
      'MaterialIcons',
      '$flutterRoot/bin/cache/artifacts/material_fonts/MaterialIcons-Regular.otf',
    );
  }
  await testMain();
}
