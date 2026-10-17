import 'package:digitalbrain_flutter/src/node_contracts.dart';
import 'package:test/test.dart';

void main() {
  test('every app UI kind maps to its contract and change signal', () {
    expect(
      NodeContracts.of('button').contract,
      'DigitalBrain.Flutter.Button.IButton',
    );
    expect(NodeContracts.of('button').changed, 'ButtonChanged');
    expect(
      NodeContracts.of('view').contract,
      'DigitalBrain.Flutter.Views.IView',
    );
    expect(NodeContracts.of('view').changed, 'ViewChanged');
    expect(NodeContracts.of('surface').changed, 'SurfaceChanged');
    expect(
      NodeContracts.of('scene').contract,
      'DigitalBrain.Flame.Scene.IScene',
    );
    expect(NodeContracts.of('scene').changed, 'SceneChanged');
    expect(
      NodeContracts.of('collection').contract,
      'DigitalBrain.Flutter.Collection.ICollectionView',
    );
    expect(() => NodeContracts.of('nope'), throwsArgumentError);
  });
}
