import 'package:intochat_os_flutter_shell/src/core/node_contracts.dart';
import 'package:test/test.dart';

void main() {
  test('every app UI kind maps to its contract and change signal', () {
    expect(
      NodeContracts.of('view').contract,
      'IntoChat.OS.Modules.Flutter.Views.IView',
    );
    expect(NodeContracts.of('view').changed, 'FlutterViewChanged');
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
    for (final kind in ['webbrowser', 'confirmation']) {
      expect(NodeContracts.tryResolve(kind), isNotNull, reason: kind);
    }
  });

  test('the per-widget kinds are gone', () {
    for (final kind in [
      'button',
      'text',
      'surface',
      'scene',
      'toggle',
      'chat',
      'composer',
    ]) {
      expect(() => NodeContracts.of(kind), throwsArgumentError, reason: kind);
    }
  });
}
