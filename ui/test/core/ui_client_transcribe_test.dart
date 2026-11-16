import 'dart:convert';
import 'dart:typed_data';

import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:test/test.dart';

void main() {
  test('transcribe posts audio bytes with auth and parses text', () async {
    late http.Request captured;
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://127.0.0.1:5080'),
      presenceToken: 'presence-proof-token',
      httpClient: MockClient((request) async {
        captured = request;
        return http.Response(
          jsonEncode({'text': 'deploy to staging', 'model': 'whisper-local'}),
          200,
          headers: {'content-type': 'application/json'},
        );
      }),
    );
    addTearDown(client.close);

    final wav = Uint8List.fromList([0x52, 0x49, 0x46, 0x46]);
    final text = await client.transcribe(wav, 'voice.wav');

    expect(text, 'deploy to staging');
    expect(captured.method, 'POST');
    expect(captured.url.path, '/ai/transcriptions');
    expect(captured.url.queryParameters['fileName'], 'voice.wav');
    expect(captured.headers['content-type'], startsWith('audio/wav'));
    expect(captured.headers['X-IntoChat-Presence'], 'presence-proof-token');
    expect(captured.bodyBytes, wav);
  });

  test('transcribe surfaces 503 reason via UiRequestException', () async {
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://127.0.0.1:5080'),
      httpClient: MockClient(
        (_) async => http.Response(
          jsonEncode({
            'status': 503,
            'detail': 'Voice-to-text is not configured.',
          }),
          503,
          headers: {'content-type': 'application/problem+json'},
        ),
      ),
    );
    addTearDown(client.close);

    await expectLater(
      client.transcribe(Uint8List.fromList([1, 2, 3]), 'voice.wav'),
      throwsA(
        isA<UiRequestException>()
            .having((e) => e.statusCode, 'statusCode', 503)
            .having(
              (e) => e.detail,
              'detail',
              'Voice-to-text is not configured.',
            ),
      ),
    );
  });
}
