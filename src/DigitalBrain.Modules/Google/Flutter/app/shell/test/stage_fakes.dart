import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

/// Records every call and serves empty scenes and an empty stage.
class FakeSceneClient implements SceneClient {
  final calls = <String>[];

  @override
  String get workspace => 'brain';

  @override
  Future<SceneState> read(String sceneKey) async {
    calls.add('read $sceneKey');
    return SceneState.fromJson({'name': sceneKey});
  }

  @override
  Future<void> fire(String sceneKey, String event, {String? nodeId}) async =>
      calls.add('fire $sceneKey $event');

  @override
  Future<void> report(
    String sceneKey,
    String nodeId,
    String kind,
    double x,
    double y, {
    String? payload,
  }) async => calls.add('report $sceneKey $nodeId $kind');

  @override
  Future<StageState> stage() async {
    calls.add('stage');
    return const StageState(name: 's', revision: 0);
  }

  @override
  Future<void> go(String sceneKey, {String? dockSurfaceKey}) async =>
      calls.add('go $sceneKey');

  @override
  Future<void> back() async => calls.add('back');

  @override
  Future<void> home() async => calls.add('home');

  @override
  Future<void> setHome(String sceneKey) async => calls.add('setHome $sceneKey');
}

/// A UI client whose every request answers 404, for widgets that only need one to exist.
DigitalBrainUiClient fakeUiClient() => DigitalBrainUiClient(
  baseUri: Uri.parse('http://localhost'),
  httpClient: MockClient((_) async => http.Response('', 404)),
);
