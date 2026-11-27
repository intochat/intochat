import 'dart:convert';

import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:grpc/grpc.dart' as grpc;
import 'package:grpc/grpc_connection_interface.dart' show ClientChannelBase;

import '../generated/neurons.pb.dart' as pb;
import '../generated/neurons.pbgrpc.dart' as rpc;
import 'neuron_channel_io.dart'
    if (dart.library.js_interop) 'neuron_channel_web.dart'
    as transport;

/// One signal as the channel delivers it; [payload] is the signal's JSON.
final class NeuronSignal {
  const NeuronSignal({
    required this.source,
    required this.type,
    required this.publisher,
    required this.sequence,
    required this.payload,
    required this.serverTimeMs,
  });
  final String source, type, publisher;
  final int sequence, serverTimeMs;
  final Map<String, dynamic> payload;

  factory NeuronSignal.fromEnvelope(pb.SignalEnvelope e) => NeuronSignal(
    source: e.source,
    type: e.type,
    publisher: e.publisher,
    sequence: e.sequence.toInt(),
    payload: e.payload.isEmpty
        ? const {}
        : jsonDecode(utf8.decode(e.payload)) as Map<String, dynamic>,
    serverTimeMs: e.serverTimeMs.toInt(),
  );
}

/// A source to watch: its contract, its brain-scoped key and the signal types wanted.
final class WatchSourceSpec {
  const WatchSourceSpec(this.contract, this.key, this.types);
  final String contract, key;
  final List<String> types;
}

/// The operations the OS has: call a contract, watch a source, read inspectable state. [NeuronChannel] carries them
/// over gRPC(-Web); a test supplies its own link.
abstract interface class NeuronLink {
  Future<Map<String, dynamic>?> call(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments,
  );
  Future<dynamic> readState(
    String brainId,
    String contract,
    String key, {
    Duration? timeout,
  });
  Stream<NeuronSignal> watch(String brainId, List<WatchSourceSpec> sources);
  Future<void> shutdown();
}

/// Centralized gRPC interceptor injecting auth + session headers on every RPC
/// and applying [defaultUnaryTimeout] to unary calls while leaving streaming
/// calls unbounded and preserving explicit per-call [grpc.CallOptions] overrides.
final class _NeuronAuthAndDeadlineInterceptor extends grpc.ClientInterceptor {
  _NeuronAuthAndDeadlineInterceptor({
    required this.authorization,
    required this.sessionHeaders,
    required this.defaultUnaryTimeout,
  });

  final String? authorization;
  final Map<String, String> Function()? sessionHeaders;
  final Duration defaultUnaryTimeout;

  grpc.CallOptions _enrich(
    grpc.CallOptions options, {
    required bool applyDefaultDeadline,
  }) {
    final defaults = grpc.CallOptions(
      metadata: authorization == null
          ? const {}
          : {'authorization': authorization!},
      timeout: applyDefaultDeadline ? defaultUnaryTimeout : null,
      providers: [
        if (sessionHeaders != null)
          (metadata, uri) {
            for (final entry in sessionHeaders!().entries) {
              metadata.putIfAbsent(entry.key, () => entry.value);
            }
          },
      ],
    );
    return defaults.mergedWith(options);
  }

  @override
  grpc.ResponseFuture<R> interceptUnary<Q, R>(
    grpc.ClientMethod<Q, R> method,
    Q request,
    grpc.CallOptions options,
    grpc.ClientUnaryInvoker<Q, R> invoker,
  ) => invoker(method, request, _enrich(options, applyDefaultDeadline: true));

  @override
  grpc.ResponseStream<R> interceptStreaming<Q, R>(
    grpc.ClientMethod<Q, R> method,
    Stream<Q> requests,
    grpc.CallOptions options,
    grpc.ClientStreamingInvoker<Q, R> invoker,
  ) => invoker(method, requests, _enrich(options, applyDefaultDeadline: false));
}

/// The operations over one gRPC(-Web) connection.
final class NeuronChannel implements NeuronLink {
  NeuronChannel(
    Uri baseUri, {
    String? authorization,
    Map<String, String> Function()? sessionHeaders,
    this.callTimeout = defaultCallTimeout,
  }) : _channel = transport.createNeuronChannel(baseUri),
       _interceptor = _NeuronAuthAndDeadlineInterceptor(
         authorization: authorization,
         sessionHeaders: sessionHeaders,
         defaultUnaryTimeout: callTimeout,
       ) {
    _client = rpc.NeuronsClient(_channel, interceptors: [_interceptor]);
  }

  /// The deadline a unary [call] or [readState] gets unless it names its own;
  /// `Watch` streams have none.
  static const defaultCallTimeout = Duration(seconds: 15);
  final Duration callTimeout;
  final ClientChannelBase _channel;
  final _NeuronAuthAndDeadlineInterceptor _interceptor;
  late final rpc.NeuronsClient _client;

  /// Exposes the centralized interceptor for unit tests.
  @visibleForTesting
  grpc.ClientInterceptor get interceptorForTesting => _interceptor;

  static pb.CallRequest callRequest(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments,
  ) => pb.CallRequest(
    brainId: brainId,
    contract: contract,
    key: key,
    method: method,
    arguments: utf8.encode(jsonEncode(arguments)),
  );

  /// Runs a contract method; null when the method answers nothing. It fails with
  /// `DEADLINE_EXCEEDED` after [timeout] (default [callTimeout]).
  @override
  Future<Map<String, dynamic>?> call(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments, {
    Duration? timeout,
  }) async {
    final reply = await _client.call(
      callRequest(brainId, contract, key, method, arguments),
      options: timeout == null ? null : grpc.CallOptions(timeout: timeout),
    );
    if (reply.result.isEmpty) return null;
    final decoded = jsonDecode(utf8.decode(reply.result));
    return decoded is Map<String, dynamic> ? decoded : {'value': decoded};
  }

  /// Reads a neuron's inspectable state; null when empty or not implemented.
  @override
  Future<dynamic> readState(
    String brainId,
    String contract,
    String key, {
    Duration? timeout,
  }) async {
    final reply = await _client.readState(
      pb.NeuronStateRequest(brainId: brainId, contract: contract, key: key),
      options: timeout == null ? null : grpc.CallOptions(timeout: timeout),
    );
    if (reply.stateJson.isEmpty) return null;
    return jsonDecode(utf8.decode(reply.stateJson));
  }

  /// The sources' signals: each one's retained last signal first, then live ones.
  @override
  Stream<NeuronSignal> watch(String brainId, List<WatchSourceSpec> sources) {
    final request = pb.WatchRequest(brainId: brainId)
      ..sources.addAll([
        for (final s in sources)
          pb.WatchSource(
            contract: s.contract,
            key: s.key,
            signalTypes: s.types,
          ),
      ]);
    return _client.watch(request).map(NeuronSignal.fromEnvelope);
  }

  @override
  Future<void> shutdown() => _channel.shutdown();
}
