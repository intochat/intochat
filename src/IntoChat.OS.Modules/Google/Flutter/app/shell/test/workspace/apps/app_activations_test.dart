import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/workspace/apps/app_activations.dart';
import 'package:intochat_os_flutter_shell/workspace/apps/apps_request.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

typedef _Call = ({String method, String path, Object? body});

void main() {
  late List<_Call> calls;
  setUp(() => calls = []);

  AppsRequest answering(Object? Function(String method, String path) answer) =>
      (method, path, [body]) async {
        calls.add((method: method, path: path, body: body));
        return answer(method, path);
      };

  AppsRequest failing(int status, Map<String, Object?> body) =>
      (method, path, [request]) async {
        calls.add((method: method, path: path, body: request));
        throw UiRequestException(method, path, status, jsonEncode(body));
      };

  test('install_posts_the_request_and_opens_the_app', () async {
    final request = {
      'ref': {'publisher': 'os', 'name': 'notes', 'version': '1.0.0'},
      'consent': 'fixture-value-0001',
    };
    final result = await runAppActivation({
      'kind': 'install',
      'brainId': 'b 1',
      'title': 'Notes',
      'request': request,
    }, answering((_, _) => {'status': 'ok', 'installedName': 'notes'}));

    expect(calls.single.method, 'POST');
    expect(calls.single.path, '/brains/b%201/apps/install');
    expect(calls.single.body, request);
    expect(result.toast, 'Installed Notes.');
    expect(result.tone, UiTone.done);
    expect(result.open, 'notes');
  });

  test('conflict_shows_nothing', () async {
    final result = await runAppActivation({
      'kind': 'install',
      'brainId': 'b',
      'request': {'ref': {}},
    }, failing(409, {'status': 'needsConsent', 'requires': []}));
    expect(result.toast, isEmpty);
    expect(result.open, isNull);
  });

  test('forbidden_shows_server_copy', () async {
    final result = await runAppActivation(
      {
        'kind': 'install',
        'brainId': 'b',
        'request': {'ref': {}},
      },
      failing(403, {
        'status': 'refused',
        'error': 'Only you can install apps.',
      }),
    );
    expect(result.toast, 'Only you can install apps.');
    expect(result.tone, UiTone.error);

    final other = await runAppActivation({
      'kind': 'install',
      'brainId': 'b',
      'request': {'ref': {}},
    }, failing(500, {'error': 'System.Exception: boom at line 1'}));
    expect(other.toast, 'That app could not be installed.');
    expect(other.open, isNull);
  });

  test('update_and_remove_call_their_routes', () async {
    final updated = await runAppActivation({
      'kind': 'update',
      'brainId': 'b',
      'name': 'notes',
      'title': 'Notes',
      'consent': 'fixture-value-0002',
    }, answering((_, _) => {'name': 'notes'}));
    expect(calls.last.method, 'POST');
    expect(calls.last.path, '/brains/b/apps/notes/update');
    expect(calls.last.body, {'consent': 'fixture-value-0002'});
    expect(updated.toast, 'Updated Notes.');
    expect(updated.open, isNull);

    final removed = await runAppActivation({
      'kind': 'remove',
      'brainId': 'b',
      'name': 'notes',
      'title': 'Notes',
    }, answering((_, _) => {'deleted': 'notes'}));
    expect(calls.last.method, 'DELETE');
    expect(calls.last.path, '/brains/b/apps/notes');
    expect(removed.toast, 'Removed Notes.');
  });

  test('configuration_view_opens_the_apps_detail', () {
    expect(
      configurationActivation({
        'brainId': 'b',
        'application': 'notes',
        'view': 'configuration',
      }),
      {
        'brainId': 'b',
        'kind': 'workspace',
        'application': 'apps',
        'operation': 'open',
        'input': 'installed:notes',
      },
    );
  });
}
