import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/auth/brain_session_gate.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/src/core/presence_fragment.dart';

void main() {
  tearDown(() {
    IntoChatOSHostEnv.setCliArguments(const []);
  });

  group('Desktop presence token resolution & header transport', () {
    test(
      'resolves INTOCHAT_PRESENCE_TOKEN from process environment and CLI args',
      () {
        expect(
          IntoChatOSHostEnv.resolvePresenceToken(
            processEnvironment: {'INTOCHAT_PRESENCE_TOKEN': 'env-token-123'},
          ),
          'env-token-123',
        );

        IntoChatOSHostEnv.setCliArguments(['--presence-token=cli-token-456']);
        expect(
          IntoChatOSHostEnv.resolvePresenceToken(
            processEnvironment: {'INTOCHAT_PRESENCE_TOKEN': 'env-token-123'},
          ),
          'cli-token-456',
        );

        IntoChatOSHostEnv.setCliArguments([
          '--presence-token',
          'cli-space-789',
        ]);
        expect(
          IntoChatOSHostEnv.resolvePresenceToken(processEnvironment: const {}),
          'cli-space-789',
        );
      },
    );

    test(
      'attaches X-IntoChat-Presence header to HTTP and SSE requests',
      () async {
        final seenHeaders = <String, Map<String, String>>{};

        final mockHttp = MockClient.streaming((request, bodyStream) async {
          await bodyStream.drain<void>();
          seenHeaders['${request.method} ${request.url.path}'] = Map.of(
            request.headers,
          );
          if (request.url.path.endsWith('/brain/events')) {
            final bytes = utf8.encode(
              'event: brain-snapshot\ndata: {"rootId":"local","observedAt":"2026-11-14T12:00:00Z","nodes":[],"synapses":[]}\n\n',
            );
            return http.StreamedResponse(Stream.value(bytes), 200);
          }
          return http.StreamedResponse(
            Stream.value(
              utf8.encode(
                '{"principalId":"owner","accountId":"local","brainId":"local"}',
              ),
            ),
            200,
          );
        });

        final client = IntoChatOSUiClient.fromEnvironment(
          httpClient: mockHttp,
          processEnvironment: {
            'INTOCHATOS_UI_BASE': 'http://127.0.0.1:5080',
            'INTOCHAT_PRESENCE_TOKEN': 'secret-presence-proof',
          },
        );
        addTearDown(client.close);

        await client.readSession();
        final snapshot = await client.watchBrain(brainId: 'local').first;
        expect(snapshot.nodes, isEmpty);

        expect(
          seenHeaders['GET /identity/session']?['X-IntoChat-Presence'],
          'secret-presence-proof',
        );
        expect(
          seenHeaders['GET /brains/local/brain/events']?['X-IntoChat-Presence'],
          'secret-presence-proof',
        );
      },
    );
  });

  group('Web #presence=<token> fragment stripping & cookie exchange', () {
    test(
      'parsePresenceFragment extracts token and strips it from hash cleanly',
      () {
        expect(parsePresenceFragment(''), isNull);
        expect(parsePresenceFragment('#'), isNull);
        expect(parsePresenceFragment('#/open?app=ino'), isNull);

        expect(parsePresenceFragment('#presence=tok_abc-123'), (
          token: 'tok_abc-123',
          remainingHash: '',
        ));

        expect(parsePresenceFragment('#foo=1&presence=tok%2B2&bar=3'), (
          token: 'tok+2',
          remainingHash: '#foo=1&bar=3',
        ));

        expect(parsePresenceFragment('#/open?app=ino&presence=tok_xyz'), (
          token: 'tok_xyz',
          remainingHash: '#/open?app=ino',
        ));

        expect(parsePresenceFragment('#/?presence=tok_root'), (
          token: 'tok_root',
          remainingHash: '',
        ));
      },
    );

    testWidgets(
      'BrainSessionGate exchanges web presence token via POST /auth/local-presence and sends IntoChatPresence cookie on subsequent requests',
      (tester) async {
        final callOrder = <String>[];
        Map<String, dynamic>? postedBody;
        String? sessionCookieHeader;

        await tester.pumpWidget(
          BrainSessionGate(
            readLocalAccount: (_) async => null,
            consumeWebPresenceToken: () => 'web-fragment-token-999',
            createClient: (_) => IntoChatOSUiClient(
              baseUri: Uri.parse('http://127.0.0.1:5080'),
              httpClient: MockClient((request) async {
                callOrder.add('${request.method} ${request.url.path}');
                if (request.url.path == '/auth/local-presence') {
                  postedBody = jsonDecode(request.body) as Map<String, dynamic>;
                  return http.Response(
                    '{"ok":true}',
                    200,
                    headers: {
                      'set-cookie': 'IntoChatPresence=hmac-cookie-val; Path=/; HttpOnly; SameSite=Strict',
                    },
                  );
                }
                if (request.url.path == '/auth/check') {
                  final cookie = request.headers['cookie'] ?? '';
                  return http.Response(
                    '',
                    cookie.contains('IntoChatPresence=hmac-cookie-val')
                        ? 204
                        : 401,
                  );
                }
                if (request.url.path == '/identity/session') {
                  sessionCookieHeader = request.headers['cookie'];
                  return http.Response(
                    '{"principalId":"owner","accountId":"local","brainId":"local"}',
                    200,
                  );
                }
                return http.Response('', 404);
              }),
            ),
            builder: (client, _, _) => MaterialApp(
              home: Scaffold(
                body: Text(
                  'Signed in: ${client?.principalId}/${client?.defaultBrainId}',
                ),
              ),
            ),
          ),
        );

        await tester.pumpAndSettle();

        expect(callOrder, [
          'POST /auth/local-presence',
          'GET /auth/check',
          'GET /identity/session',
        ]);
        expect(postedBody, {'token': 'web-fragment-token-999'});
        expect(sessionCookieHeader, 'IntoChatPresence=hmac-cookie-val');
        expect(find.text('Signed in: owner/local'), findsOneWidget);
      },
    );
  });
}
