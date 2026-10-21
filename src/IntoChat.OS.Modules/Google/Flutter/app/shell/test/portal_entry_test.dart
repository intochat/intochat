import 'dart:convert';

import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/auth/brain_session_gate.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

void main() {
  for (final loginUrl in ['/auth/login', 'https://attacker.test/login']) {
    testWidgets('browser discovery accepts only same-origin entry $loginUrl', (
      tester,
    ) async {
      final opened = <Uri>[];
      var shellBuilds = 0;
      await tester.pumpWidget(
        BrainSessionGate(
          readLocalAccount: (_) async => null,
          openSignIn: (uri) async {
            opened.add(uri);
            return true;
          },
          createClient: (_) => IntoChatOSUiClient(
            baseUri: Uri.parse('https://kernel.test'),
            httpClient: MockClient((request) async {
              if (request.url.path == '/auth/entry') {
                return http.Response(jsonEncode({'loginUrl': loginUrl}), 200);
              }
              return http.Response('', 401);
            }),
          ),
          builder: (_, _, _) {
            shellBuilds++;
            return const SizedBox();
          },
        ),
      );
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(shellBuilds, 0);
      if (loginUrl.startsWith('/')) {
        expect(opened, [Uri.parse('https://kernel.test/auth/login')]);
      } else {
        expect(opened, isEmpty);
        expect(find.byKey(const Key('login-username')), findsOneWidget);
      }
    });
  }
}
