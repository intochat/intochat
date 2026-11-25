import 'package:intochat_os_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  paletteTests();
}

void paletteTests() {
  test('old theme values load as palette names and palettes round-trip', () {
    expect(WorkspacePreferences.fromJson({'theme': 'dark'}).theme, 'midnight');
    expect(WorkspacePreferences.fromJson({'theme': 'light'}).theme, 'paper');
    expect(WorkspacePreferences.fromJson({}).theme, 'intochat');
    expect(
      WorkspacePreferences.fromJson({'theme': 'system'}).theme,
      'intochat',
    );
    expect(
      WorkspacePreferences.fromJson({'theme': 'midnight'}).theme,
      'midnight',
    );
    expect(WorkspacePreferences.fromJson({'theme': 'lumen'}).theme, 'lumen');
    expect(WorkspacePreferences().toJson()['theme'], 'intochat');
  });
}
