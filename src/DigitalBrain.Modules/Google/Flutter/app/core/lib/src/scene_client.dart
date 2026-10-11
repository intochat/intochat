import 'dart:convert';

import 'package:http/http.dart' as http;

import 'models/scene_models.dart';
import 'ui_client.dart';

/// The brain's Flame routes under `/brains/{brain}/ui/`: scenes and the person's stage.
final class SceneClient {
  SceneClient(this._http, this._base, this.workspace);
  final http.Client _http;
  final Uri _base;
  final String workspace;

  String _ui(String path) =>
      '/brains/${Uri.encodeComponent(workspace)}/ui/$path';

  String _scene(String sceneKey, [String suffix = '']) =>
      _ui('scenes/${Uri.encodeComponent(sceneKey)}$suffix');

  Future<SceneState> read(String sceneKey) async =>
      SceneState.fromJson(await _getJson(_scene(sceneKey)));

  Future<void> fire(String sceneKey, String event, {String? nodeId}) =>
      _post(_scene(sceneKey, '/fire'), {'event': event, 'nodeId': nodeId});

  Future<void> report(
    String sceneKey,
    String nodeId,
    String kind,
    double x,
    double y, {
    String? payload,
  }) => _post(_scene(sceneKey, '/report'), {
    'nodeId': nodeId,
    'kind': kind,
    'x': x,
    'y': y,
    'payload': payload,
  });

  Future<StageState> stage() async =>
      StageState.fromJson(await _getJson(_ui('stage')));

  Future<void> go(String sceneKey, {String? dockSurfaceKey}) => _post(
    _ui('stage/go'),
    {'sceneKey': sceneKey, 'dockSurfaceKey': dockSurfaceKey},
  );

  Future<void> back() => _post(_ui('stage/back'), const {});

  Future<void> home() => _post(_ui('stage/home'), const {});

  Future<void> setHome(String sceneKey) =>
      _post(_ui('stage/home/set'), {'sceneKey': sceneKey});

  Future<Map<String, dynamic>> _getJson(String path) async {
    final response = await _send('GET', path);
    return jsonDecode(response.body) as Map<String, dynamic>;
  }

  Future<void> _post(String path, Map<String, Object?> body) =>
      _send('POST', path, body);

  Future<http.Response> _send(
    String method,
    String path, [
    Map<String, Object?>? body,
  ]) async {
    final request = http.Request(method, _base.resolve(path));
    if (body != null) {
      request.headers['content-type'] = 'application/json';
      request.body = jsonEncode(body);
    }
    final response = await http.Response.fromStream(await _http.send(request));
    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw UiRequestException(
        method,
        path,
        response.statusCode,
        response.body,
      );
    }
    return response;
  }
}
