import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:test/test.dart';

class TailServer extends http.BaseClient {
  TailServer(this.connections);

  /// One entry per connection: the events it sends before it ends, or a status to refuse it with.
  final List<Object> connections;
  final requested = <Uri>[];

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    requested.add(request.url);
    final next = connections.isEmpty ? <Map>[] : connections.removeAt(0);
    if (next is int) return http.StreamedResponse(const Stream.empty(), next);
    final body = (next as List)
        .map(
          (batch) => 'event: journaled-signal\ndata: ${jsonEncode(batch)}\n\n',
        )
        .join();
    return http.StreamedResponse(Stream.value(utf8.encode(body)), 200);
  }
}

Map<String, Object?> signal(int seq, {String alias = 'ctr.bump'}) => {
  'seq': seq,
  'publisher': 'b1/ctr/default',
  'alias': alias,
  'traceId': 'trace-$seq',
  'causationId': null,
  'at': '2026-11-08T10:00:00+00:00',
};

void main() {
  final base = Uri.parse('http://localhost:5000');

  test('parses a batch', () async {
    final server = TailServer([
      [
        {
          'signals': [signal(1), signal(2, alias: 'ctr.kick')],
        },
      ],
    ]);
    final client = JournalTailClient(baseUri: base, httpClient: server);

    final batches = await client.watch('b1').take(1).toList();

    expect(batches.single.map((s) => s.seq), [1, 2]);
    expect(batches.single.last.alias, 'ctr.kick');
    expect(batches.single.first.publisher, 'b1/ctr/default');
    expect(batches.single.first.traceId, 'trace-1');
    expect(batches.single.first.causationId, isNull);
    expect(batches.single.first.at.toUtc().hour, 10);
    expect(server.requested.single.path, '/brains/b1/journal/tail');
    expect(server.requested.single.queryParameters['since'], '0');
  });

  test('reconnects from the last sequence seen', () async {
    final server = TailServer([
      [
        {
          'signals': [signal(4), signal(5)],
        },
      ],
      [
        {
          'signals': [signal(6)],
        },
      ],
    ]);
    final client = JournalTailClient(
      baseUri: base,
      httpClient: server,
      retryDelay: const Duration(milliseconds: 1),
    );

    final batches = await client.watch('b1', since: 3).take(2).toList();

    expect(batches.map((b) => b.map((s) => s.seq).toList()), [
      [4, 5],
      [6],
    ]);
    expect(server.requested.map((u) => u.queryParameters['since']), ['3', '5']);
  });

  test('a refusal ends the stream with an error and is not retried', () async {
    final server = TailServer([403, 403]);
    final client = JournalTailClient(
      baseUri: base,
      httpClient: server,
      retryDelay: const Duration(milliseconds: 1),
    );

    await expectLater(
      client.watch('b1'),
      emitsError(isA<JournalTailRefused>()),
    );
    expect(server.requested, hasLength(1));
  });

  test('a server error is retried', () async {
    final server = TailServer([
      503,
      [
        {
          'signals': [signal(1)],
        },
      ],
    ]);
    final client = JournalTailClient(
      baseUri: base,
      httpClient: server,
      retryDelay: const Duration(milliseconds: 1),
    );

    final batches = await client.watch('b1').take(1).toList();

    expect(batches.single.single.seq, 1);
    expect(server.requested, hasLength(2));
  });

  test('a malformed frame backs off and stalls after five failures', () async {
    final server = _Malformed();
    final client = JournalTailClient(
      baseUri: base,
      httpClient: server,
      retryDelay: const Duration(milliseconds: 1),
      maxDelay: const Duration(milliseconds: 4),
    );

    await expectLater(
      client.watch('b1', since: 7),
      emitsInOrder([emitsError(isA<JournalTailStalled>()), emitsDone]),
    );
    expect(server.requested, hasLength(JournalTailClient.maxFailures));
    expect(
      server.requested.map((url) => url.queryParameters['since']).toSet(),
      {'7'},
    );
    expect(
      [
        for (var failures = 1; failures <= 6; failures++)
          client.backoff(failures).inMilliseconds,
      ],
      [1, 2, 4, 4, 4, 4],
    );
    expect(
      JournalTailClient(baseUri: base, httpClient: server).backoff(10),
      const Duration(seconds: 30),
    );
  });

  test('the ui client reads a unit state and view, null on 404', () async {
    final ui = IntoChatOSUiClient(
      baseUri: base,
      httpClient: _Reads({
        '/brains/b1/state': (200, {'bumps': 3}),
        '/brains/b1/view': (
          200,
          {
            'alias': 'ctr',
            'generation': 2,
            'view': {'kind': 'Row'},
          },
        ),
      }),
    );

    expect(await ui.readUnitState('b1', 'ctr'), {'bumps': 3});
    final view = await ui.readUnitView('b1', 'ctr');
    expect(view!.generation, 2);
    expect(view.view['kind'], 'Row');
    expect(await ui.readUnitState('b2', 'ctr'), isNull);
  });
}

/// Answers every connection with a frame whose data is not JSON.
class _Malformed extends http.BaseClient {
  final requested = <Uri>[];

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    requested.add(request.url);
    return http.StreamedResponse(
      Stream.value(utf8.encode('event: journaled-signal\ndata: {not json\n\n')),
      200,
    );
  }
}

class _Reads extends http.BaseClient {
  _Reads(this.routes);
  final Map<String, (int, Object)> routes;

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final route = routes[request.url.path];
    if (route == null) return http.StreamedResponse(const Stream.empty(), 404);
    return http.StreamedResponse(
      Stream.value(utf8.encode(jsonEncode(route.$2))),
      route.$1,
      headers: {'content-type': 'application/json'},
    );
  }
}
