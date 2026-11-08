import 'package:intochat_os_flutter/src/node_contracts.dart';
import 'package:test/test.dart';

void main() {
  test('every app UI kind maps to its contract and change signal', () {
    expect(
      NodeContracts.of('view').contract,
      'IntoChat.OS.Modules.Flutter.Views.IView',
    );
    expect(NodeContracts.of('view').changed, 'ViewChanged');
    expect(
      NodeContracts.of('field').contract,
      'IntoChat.OS.Modules.Flutter.Fields.IField',
    );
    expect(NodeContracts.of('field').changed, 'FieldChanged');
    expect(
      NodeContracts.of('viewport').contract,
      'IntoChat.OS.Modules.Flutter.Viewport.IViewport',
    );
    expect(NodeContracts.of('viewport').changed, 'ViewportChanged');
    for (final kind in ['chat', 'composer', 'webbrowser', 'confirmation']) {
      expect(NodeContracts.tryResolve(kind), isNotNull, reason: kind);
    }
  });

  test('the per-widget kinds are gone', () {
    for (final kind in ['button', 'text', 'surface', 'scene', 'toggle']) {
      expect(() => NodeContracts.of(kind), throwsArgumentError, reason: kind);
    }
  });
}
