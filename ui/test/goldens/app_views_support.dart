import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

/// Serves a C# `ViewFixture` JSON map (`appId`, `scenario`, `side`, `main`) as
/// the app's published view: the `DigitalBrain.UI.View` JSON goes to the client
/// unchanged, as `GET /apps/node?kind=view` answers `{generation, view}`.
({IntoChatOSUiClient client, Map<String, dynamic> surface})
surfaceClientFromFixture(Map<String, dynamic> fixture) {
  final appId = (fixture['appId'] as String? ?? 'app').trim();
  final viewName = 'local/apps/$appId/view';
  final view = viewFromFixture(fixture);
  final mockHttp = MockClient((request) async {
    final body = request.url.path.endsWith('/node')
        ? {'generation': 1, 'view': view}
        : const <String, dynamic>{};
    return http.Response.bytes(
      utf8.encode(jsonEncode(body)),
      200,
      headers: {'content-type': 'application/json; charset=utf-8'},
    );
  });
  return (
    client: IntoChatOSUiClient(
      baseUri: Uri.parse('http://localhost:8080/'),
      httpClient: mockHttp,
    ),
    surface: {'kind': 'view', 'name': viewName},
  );
}

/// The fixture's main view; with a side view, the two side by side as the
/// shell lays out `{app}/side` beside `{app}/view`.
Map<String, dynamic> viewFromFixture(Map<String, dynamic> fixture) {
  final main = Map<String, dynamic>.from(fixture['main'] as Map);
  final side = fixture['side'];
  if (side is! Map) return main;
  return {
    'title': main['title'] ?? '',
    'root': {
      'kind': 'row',
      'gap': 16,
      'extents': [280, 1],
      'children': [side['root'], main['root']],
    },
  };
}
