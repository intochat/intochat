import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('House Rule #21: files under nodes/ and chat/ contain no RegExp(, .toLowerCase(), or == string literals', () {
    final violations = <String>[];
    final dirs = [Directory('lib/src/nodes'), Directory('lib/src/chat')];
    final stringLiteralEq = RegExp(
      r'''(?:==|!=)\s*['"][^'"]*['"]|['"][^'"]*['"]\s*(?:==|!=)''',
    );
    final stringSwitchArm = RegExp(
      r'''(?:^\s*['"][^'"]*['"]\s*=>|\bcase\s*['"][^'"]*['"]\s*:)''',
    );

    for (final dir in dirs) {
      expect(dir.existsSync(), isTrue, reason: '${dir.path} must exist');
      for (final entity in dir.listSync(recursive: true).whereType<File>()) {
        if (!entity.path.endsWith('.dart')) continue;
        final lines = entity.readAsLinesSync();
        for (var i = 0; i < lines.length; i++) {
          final line = lines[i];
          if (line.contains('RegExp(')) {
            violations.add('${entity.path}:${i + 1}: RegExp( -> $line');
          }
          if (line.contains('.toLowerCase()')) {
            violations.add('${entity.path}:${i + 1}: .toLowerCase() -> $line');
          }
          if (line.contains('.split(')) {
            violations.add('${entity.path}:${i + 1}: .split( -> $line');
          }
          if (stringLiteralEq.hasMatch(line)) {
            violations.add(
              '${entity.path}:${i + 1}: string == comparison -> $line',
            );
          }
          if (stringSwitchArm.hasMatch(line)) {
            violations.add(
              '${entity.path}:${i + 1}: string switch arm -> $line',
            );
          }
        }
      }
    }

    expect(violations, isEmpty, reason: violations.join('\n'));
  });

  test('P2-11: brand_visuals.dart keeps asset path maps only without duplicated brand/model tables', () {
    final source = File('lib/src/theme/brand_visuals.dart').readAsStringSync();
    expect(source.contains('resolveModelVisual'), isFalse);
    expect(source.contains('_resolveHumanModelLabel'), isFalse);
    expect(
      source.contains('static const Map<String, BrandVisualSpec> catalog'),
      isFalse,
    );
    expect(
      source.contains('static const Map<String, BrandVisualSpec> providers'),
      isFalse,
    );
    expect(source.contains('_aliases'), isFalse);
  });
}
