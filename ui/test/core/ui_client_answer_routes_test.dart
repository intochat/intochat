import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:test/test.dart';

// A person's action is a command on `POST /act`; a confirmation answer and a
// browser report have their own routes; there is no other UI event route.
void main() {
  late List<http.Request> sent;
  late IntoChatOSUiClient client;

  setUp(() {
    sent = [];
    client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://127.0.0.1:5080'),
      presenceToken: 'presence-proof-token',
      httpClient: MockClient((request) async {
        sent.add(request);
        return http.Response(
          jsonEncode({'delivered': true}),
          200,
          headers: {'content-type': 'application/json'},
        );
      }),
    );
  });
  tearDown(() => client.close());

  test('a confirmation answer posts to the plan of the brain inbox', () async {
    await client.uiEvent('owner', {
      'kind': 'confirmation',
      'planId': 'cb-codeblocks/run 1',
      'approve': true,
    });

    final request = sent.single;
    expect(request.method, 'POST');
    expect(
      request.url.toString(),
      'http://127.0.0.1:5080/brains/owner/confirmations/cb-codeblocks/run%201',
    );
    expect(jsonDecode(request.body), {'approve': true});
  });

  test('browser reports post to the brain browser routes', () async {
    await client.uiEvent('owner', {
      'kind': 'webbrowser',
      'action': 'connect',
      'name': 'owner/apps/researcher/browser',
      'port': 9222,
      'sessionId': 's-1',
    });
    await client.uiEvent('owner', {
      'kind': 'webbrowser',
      'action': 'disconnect',
      'name': 'owner/apps/researcher/browser',
      'sessionId': 's-1',
    });
    await client.uiEvent('owner', {
      'kind': 'webbrowser',
      'action': 'unavailable',
      'name': 'owner/apps/researcher/browser',
      'reason': 'No WebView2 runtime.',
    });

    expect(sent.map((request) => request.url.path), [
      '/brains/owner/browsers/connect',
      '/brains/owner/browsers/disconnect',
      '/brains/owner/browsers/unavailable',
    ]);
    expect(sent.map((request) => jsonDecode(request.body)), [
      {
        'name': 'owner/apps/researcher/browser',
        'port': 9222,
        'sessionId': 's-1',
      },
      {'name': 'owner/apps/researcher/browser', 'sessionId': 's-1'},
      {
        'name': 'owner/apps/researcher/browser',
        'reason': 'No WebView2 runtime.',
      },
    ]);
  });

  test('an act posts its command to the brain act route', () async {
    await client.uiEvent('owner', {
      'kind': 'act',
      'neuron': 'owner/units/bank',
      'target': '.',
      'signal': 'bank.deposit',
      'args': {'amount': 5},
      'value': '5',
    });

    final request = sent.single;
    expect(request.url.path, '/brains/owner/act');
    expect(jsonDecode(request.body), {
      'neuron': 'owner/units/bank',
      'target': '.',
      'signal': 'bank.deposit',
      'args': {'amount': 5},
      'value': '5',
    });
  });

  test('a key activation is refused before anything is sent', () async {
    await expectLater(
      () => client.uiEvent('owner', {
        'kind': 'view',
        'name': 'owner/apps/tasks/view',
        'generation': 1,
        'key': 'cancel',
      }),
      throwsArgumentError,
    );
    expect(sent, isEmpty);
  });

  test(
    'an unknown browser action is refused before anything is sent',
    () async {
      await expectLater(
        () => client.uiEvent('owner', {
          'kind': 'webbrowser',
          'action': 'navigate',
          'name': 'owner/apps/researcher/browser',
        }),
        throwsArgumentError,
      );
      expect(sent, isEmpty);
    },
  );
}
