import 'package:intochat_os_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  paletteTests();
}

void paletteTests() {
  test('legacy theme values normalize to canonical presets and round-trip', () {
    expect(
      WorkspacePreferences.fromJson({'theme': 'dark'}).theme,
      'warm-paper',
    );
    expect(
      WorkspacePreferences.fromJson({'theme': 'light'}).theme,
      'warm-paper',
    );
    expect(WorkspacePreferences.fromJson({}).theme, 'intochat');
    expect(
      WorkspacePreferences.fromJson({'theme': 'system'}).theme,
      'intochat',
    );
    expect(
      WorkspacePreferences.fromJson({'theme': 'midnight'}).theme,
      'warm-paper',
    );
    expect(
      WorkspacePreferences.fromJson({'theme': 'paper'}).theme,
      'warm-paper',
    );
    expect(
      WorkspacePreferences.fromJson({'theme': 'lumen'}).theme,
      'cyber-synaptic',
    );
    expect(
      WorkspacePreferences.fromJson({'theme': 'fluent-acrylic'}).theme,
      'fluent-acrylic',
    );
    expect(WorkspacePreferences().toJson()['theme'], 'intochat');
  });
}
