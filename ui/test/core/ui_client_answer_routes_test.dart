import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:test/test.dart';

// A confirmation answer and a browser report are not UI events: the client
// posts them to their own routes and never to `/apps/event`.
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
