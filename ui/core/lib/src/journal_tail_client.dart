import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;

import 'models/journal_models.dart';

/// Thrown into the tail stream when the server refuses it for good (no session, not a member, no such route). Other
/// failures are retried.
final class JournalTailRefused implements Exception {
  const JournalTailRefused(this.statusCode);
  final int statusCode;

  @override
  String toString() => 'The journal tail was refused ($statusCode).';
}

/// The brain journal as batches of signals, from `GET /brains/{id}/journal/tail?since=` (server-sent `journaled-signal`
/// events, no payloads). A dropped connection is reopened after [retryDelay] from the last sequence seen, so a batch is
/// never delivered twice and none is skipped.
final class JournalTailClient {
  JournalTailClient({
    required this.baseUri,
    required http.Client httpClient,
    this.retryDelay = const Duration(seconds: 1),
  }) : _http = httpClient;

  final Uri baseUri;
  final Duration retryDelay;
  final http.Client _http;

  /// The signals journaled after [since], then each new one, in batches. Cancelling the subscription ends the request.
  Stream<List<JournalTailSignal>> watch(String brainId, {int since = 0}) {
    final abort = Completer<void>();
    late StreamController<List<JournalTailSignal>> controller;
    controller = StreamController<List<JournalTailSignal>>(
      onListen: () async {
        var last = since;
        try {
          while (!abort.isCompleted) {
            try {
              await _connect(brainId, last, abort.future, (batch) {
                if (batch.isEmpty) return;
                last = batch.last.seq > last ? batch.last.seq : last;
                controller.add(batch);
              });
            } on JournalTailRefused {
              rethrow;
            } catch (_) {
              if (abort.isCompleted) break;
            }
            if (abort.isCompleted) break;
            await Future.any([Future<void>.delayed(retryDelay), abort.future]);
          }
        } catch (error, stack) {
          if (!abort.isCompleted) controller.addError(error, stack);
        } finally {
          if (!controller.isClosed) await controller.close();
        }
      },
      onCancel: () {
        if (!abort.isCompleted) abort.complete();
      },
    );
    return controller.stream;
  }

  Future<void> _connect(
    String brainId,
    int since,
    Future<void> abort,
    void Function(List<JournalTailSignal>) onBatch,
  ) async {
    final request = http.AbortableRequest(
      'GET',
      baseUri.resolve(
        '/brains/${Uri.encodeComponent(brainId)}/journal/tail?since=$since',
      ),
      abortTrigger: abort,
    )..headers['accept'] = 'text/event-stream';
    final response = await _http.send(request);
    final status = response.statusCode;
    if (status != 200) {
      await response.stream.drain<void>();
      if (status == 401 || status == 403 || status == 404 || status == 400) {
        throw JournalTailRefused(status);
      }
      throw StateError('The journal tail is unavailable ($status).');
    }
    String? event;
    final data = <String>[];
    await for (final line
        in response.stream
            .transform(utf8.decoder)
            .transform(const LineSplitter())) {
      if (line.startsWith('event:')) event = line.substring(6).trim();
      if (line.startsWith('data:')) data.add(line.substring(5).trimLeft());
      if (line.isEmpty) {
        if (event == 'journaled-signal' && data.isNotEmpty) {
          onBatch(
            JournalTailSignal.batch(
              jsonDecode(data.join('\n')) as Map<String, dynamic>,
            ),
          );
        }
        event = null;
        data.clear();
      }
    }
  }
}
