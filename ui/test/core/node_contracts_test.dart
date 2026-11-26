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
      NodeContracts.of('viewport').contract,
      'IntoChat.OS.Modules.Flutter.Viewport.IViewport',
    );
    expect(NodeContracts.of('viewport').changed, 'ViewportChanged');
    for (final kind in ['webbrowser', 'confirmation']) {
      expect(NodeContracts.tryResolve(kind), isNotNull, reason: kind);
    }
  });

  test('the per-widget and retired legacy kinds are gone', () {
    for (final kind in [
      'button',
      'text',
      'surface',
      'scene',
      'toggle',
      'chat',
      'composer',
      // A field's text travels with its command; no field neuron is watched.
      'field',
      'person',
      'collection',
      'card',
      'friends.space',
    ]) {
      expect(() => NodeContracts.of(kind), throwsArgumentError, reason: kind);
    }
  });
}
