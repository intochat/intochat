import 'dart:convert';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:http/http.dart' as http;
import 'package:test/test.dart';

class LiveServer extends http.BaseClient {
  final lives = <String>[];

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final path = request.url.path;
    if (path.endsWith('/live')) {
      lives.add(path);
      final events = lives.length == 1
          ? [
              _profile(1, replay: true),
              {'type': 'ready'},
              {'type': 'resync'},
              _profile(2),
            ]
          : [
              _profile(3, replay: true),
              {'type': 'ready'},
            ];
      return http.StreamedResponse(
        Stream.value(
          utf8.encode(events.map((e) => 'data: ${jsonEncode(e)}\n\n').join()),
        ),
        200,
        headers: {'content-type': 'text/event-stream'},
      );
    }
    return http.StreamedResponse(const Stream.empty(), 404);
  }

  static Map<String, dynamic> _profile(int sequence, {bool replay = false}) => {
    'type': 'ProfileChanged',
    'publisher': 'ada/os.profile/profile',
    'sequence': sequence,
    if (replay) 'replay': true,
    'payload': <String, dynamic>{},
  };
}

void main() {
  test('a resync reconnects the live feed and replays current state', () async {
    final server = LiveServer();
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://localhost'),
      httpClient: server,
    );
    addTearDown(client.close);
    final watch = Stopwatch()..start();
    final events = await client.watchLive('ada', ['ProfileChanged']).toList();
    expect(watch.elapsedMilliseconds, greaterThanOrEqualTo(50));
    expect(server.lives, hasLength(2));
    expect(events.map((e) => e.isReady ? 'ready' : '${e.type}:${e.sequence}'), [
      'ProfileChanged:1',
      'ready',
      'ProfileChanged:3',
      'ready',
    ]);
  });
}
