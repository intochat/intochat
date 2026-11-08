import 'dart:async';
import 'dart:convert';
import 'dart:developer' as developer;
import 'dart:typed_data';

import 'package:http/http.dart' as http;

import 'basic_credentials.dart';
import 'cookie_http_client.dart';
import 'session_http_client_io.dart'
    if (dart.library.html) 'session_http_client_web.dart'
    as transport;
import 'host_environment.dart';
import 'models/app_manifest.dart';
import 'models/installed_app.dart';
import 'models/cluster.dart';
import 'models/brain_models.dart';
import 'models/compute_usage.dart';
import 'neuron_channel.dart';
import 'neuron_watch_mux.dart';
import 'node_contracts.dart';
import 'scene_client.dart';
import 'models/session_capabilities.dart';
import 'models/table_models.dart';
import 'models/workspace_models.dart';


part 'ui_client_surfaces.dart';

final class IntoChatOSUiClient {
  /// Gated on the kernel; 404 when the kernel runs ungated.
  static const authCheckPath = '/auth/check';

  IntoChatOSUiClient({
    required this.baseUri,
    http.Client? httpClient,
    BasicCredentials? credentials,
  }) : workspaceIdentity = credentials?.username ?? 'local-owner',
       _http = httpClient is CookieHttpClient
           ? httpClient
           : CookieHttpClient(
               httpClient ?? transport.createSessionHttpClient(),
               credentials: credentials,
             ),
       _ownsClient = httpClient == null,
       _credentials = credentials;

  factory IntoChatOSUiClient.fromEnvironment({
    http.Client? httpClient,
    Map<String, String>? processEnvironment,
    BasicCredentials? credentials,
  }) {
    return IntoChatOSUiClient(
      baseUri: IntoChatOSHostEnv.requireUiBaseUri(
        processEnvironment: processEnvironment,
      ),
      httpClient: httpClient,
      credentials: credentials,
    );
  }

  /// Probes the kernel's gated `/auth/check`.
  ///
  /// Returns true when the kernel accepts the attached credentials — including
  /// the ungated case, where the endpoint is absent and any request already
  /// succeeds. Returns false only on an explicit 401, so a wrong password is
  /// distinguishable from an unreachable kernel, which throws.
  Future<bool> checkAuth() async {
    final uri = baseUri.resolve(authCheckPath);
    final streamed = await _http.send(http.Request('GET', uri));
    final response = await http.Response.fromStream(streamed);
    if (response.statusCode == 401) {
      return false;
    }
    if (response.statusCode == 204 || response.statusCode == 404) {
      return true;
    }
    throw StateError(
      'auth check failed: ${response.statusCode} ${response.body}',
    );
  }

  final Uri baseUri;

  /// Non-secret scope for local workspace preferences; never includes credentials.
  String workspaceIdentity;
  String? principalId;
  String? accountId;
  String? defaultBrainId;

  void _useSession(Map<String, dynamic> session) {
    principalId = session['principalId'] as String;
    accountId = session['accountId'] as String;
    defaultBrainId = session['brainId'] as String;
    workspaceIdentity = '$principalId|$accountId';
  }

  Future<void> readSession() async {
    final response = await _request(
      'GET',
      '/identity/session',
      timeout: const Duration(seconds: 10),
    );
    if (response.statusCode == 200 && response.body.isNotEmpty) {
      _useSession(jsonDecode(response.body) as Map<String, dynamic>);
    }
  }

  /// True while the client is on a lobby session: an anonymous visitor of a deployment that serves one
  /// (`GET /lobby/session`). The lobby brain is the default brain and its app renders on its own root, so the
  /// shell shows no chrome around it. The flag is generic: any session may carry it.
  bool chromeless = false;

  /// Set by the shell while a lobby session is open; [endLobby] calls it.
  Future<void> Function()? onLobbyEnded;

  /// Opens the lobby session when the deployment serves one: `GET /lobby/session` answers the lobby brain id
  /// (marked `chromeless: true`). Returns false for any other answer, so a host without a lobby
  /// keeps its sign-in behaviour. The visitor's lobby cookie is held by the HTTP client.
  Future<bool> readLobbySession() async {
    try {
      final response = await _http
          .get(baseUri.resolve('/lobby/session'))
          .timeout(const Duration(seconds: 5));
      if (response.statusCode != 200 || response.body.isEmpty) return false;
      final body = jsonDecode(response.body);
      final brain = body is Map<String, dynamic> ? body['brainId'] : null;
      // Only an explicit chrome-less descriptor is a lobby: a host that answers this path with a member session
      // (or anything else) is not offering one.
      if (brain is! String ||
          brain.isEmpty ||
          (body as Map<String, dynamic>)['chromeless'] != true) {
        return false;
      }
      defaultBrainId = brain;
      chromeless = true;
      return true;
    } on Object {
      return false;
    }
  }

  /// The lobby neuron raised `LobbyEnded` (the visitor signed in and the host wrote a member session): the
  /// shell drops the lobby connection and reads `/identity/session` again. Nothing of the lobby session is reused.
  Future<void> endLobby() async {
    final ended = onLobbyEnded;
    if (!chromeless || ended == null) return;
    await ended();
  }

  Future<void> signIn(
    String username,
    String password, {
    bool register = false,
  }) async {
    final response = await _request(
      'POST',
      register ? '/identity/register' : '/identity/login',
      body: {'principalId': username, 'password': password},
      timeout: const Duration(seconds: 60),
    );
    _useSession(jsonDecode(response.body) as Map<String, dynamic>);
  }

  /// Creates an owned workspace; clients never choose or claim an existing id.
  Future<String> createWorkspace() async {
    final response = await _request(
      'POST',
      '/identity/brains',
      body: const {},
      timeout: const Duration(seconds: 60),
    );
    return (jsonDecode(response.body) as Map<String, dynamic>)['brainId']
        as String;
  }

  Future<void> signOut() async {
    await _request(
      'POST',
      '/identity/logout',
      timeout: const Duration(seconds: 10),
    );
  }

  Future<void> signInLocalAccount(String username, String password) async {
    if (!['localhost', '127.0.0.1', '::1', '[::1]'].contains(baseUri.host) ||
        !['http', 'https'].contains(baseUri.scheme)) {
      throw StateError('Local accounts require a loopback server.');
    }
    http.Response response;
    try {
      response = await _request(
        'POST',
        '/identity/login',
        body: {'principalId': username, 'password': password},
        timeout: const Duration(seconds: 60),
      );
    } on UiRequestException catch (error) {
      if (error.statusCode != 401) rethrow;
      response = await _request(
        'POST',
        '/identity/register',
        body: {'principalId': username, 'password': password},
        timeout: const Duration(seconds: 60),
      );
    }
    final Object? session;
    try {
      session = jsonDecode(response.body);
    } on FormatException {
      throw StateError('Local sign-in did not return a valid session.');
    }
    if (session is! Map<String, dynamic> ||
        session['principalId'] != username ||
        session['accountId'] is! String ||
        (session['accountId'] as String).isEmpty ||
        session['brainId'] is! String ||
        (session['brainId'] as String).isEmpty) {
      throw StateError('Local sign-in did not return the requested account.');
    }
    _useSession(session);
  }

  final CookieHttpClient _http;
  final bool _ownsClient;
  final BasicCredentials? _credentials;

  /// Live updates ride the neuron channel when the client owns its transport (a real kernel),
  /// unless the build is told to keep HTTP polling (`--dart-define=INTOCHATOS_UI_TRANSPORT=http`)
  /// so the comparison can run both. A test that injects its HTTP client keeps the HTTP path,
  /// or supplies a [link] of its own.
  static const liveTransportEnv = 'INTOCHATOS_UI_TRANSPORT';
  static const _liveTransport = String.fromEnvironment(
    liveTransportEnv,
    defaultValue: 'grpc',
  );
  bool get grpcLive {
    final live = _link != null || (_ownsClient && _liveTransport != 'http');
    if (!live && !_pollingAnnounced) {
      _pollingAnnounced = true;
      developer.log(
        'live updates stay on HTTP polling (transport=$_liveTransport, ownsClient=$_ownsClient)',
        name: 'intochatos.channel',
      );
    }
    return live;
  }

  bool _pollingAnnounced = false;

  NeuronLink? _link;
  set link(NeuronLink value) => _link = value;
  final _watches = <String, NeuronWatchMux>{};
  NeuronLink get channel => _link ??= NeuronChannel(
    baseUri,
    authorization: _credentials?.authorizationHeader,
    sessionHeaders: () => {
      if (_http.cookies.isNotEmpty)
        'cookie': _http.cookies.entries
            .map((entry) => '${entry.key}=${entry.value}')
            .join('; '),
    },
  );

  /// The brain's shared watch: every node watch of a screen rides one `Watch` stream.
  NeuronWatchMux watches(String brainId) =>
      _watches[brainId] ??= NeuronWatchMux(() => channel, brainId);

  /// The node's own change signal; the view reads the node once per event.
  Stream<NeuronSignal> watchNode(String brainId, String kind, String name) {
    final friendSlot =
        (kind == 'person' && name.endsWith('/profile')) ||
        (kind == 'collection' && name.endsWith('/posts')) ||
        (kind == 'card' && RegExp(r'^.+/posts/[A-Za-z0-9_-]+$').hasMatch(name));
    final contract = NodeContracts.of(friendSlot ? 'scene' : kind);
    final source = friendSlot ? '$brainId/space' : name;
    final signals = kind == 'confirmation'
        ? ['ConfirmationRequested', 'ConfirmationResolved']
        : [contract.changed];
    return watches(
      brainId,
    ).watch(WatchSourceSpec(contract.contract, source, signals)).map((signal) {
      // Delivery latency as the comparison measures it: envelope stamp to arrival.
      developer.log(
        'neuron.latency ms=${DateTime.now().millisecondsSinceEpoch - signal.serverTimeMs} type=${signal.type} source=${signal.source}',
        name: 'intochatos.channel',
      );
      return signal;
    });
  }

  /// Server-owned capabilities; the shell hides developer-only surfaces when this is off.
  Future<SessionCapabilities> readCapabilities() async {
    final response = await _request(
      'GET',
      '/session/capabilities',
      timeout: const Duration(seconds: 10),
    );
    return SessionCapabilities.fromJson(
      jsonDecode(response.body) as Map<String, dynamic>,
    );
  }

  static String appRequestPath(String brainId, String appId) =>
      '/brains/${Uri.encodeComponent(brainId)}/apps/${Uri.encodeComponent(appId)}/request';

  /// The route that publishes an open request; the app answers through neurons, never here.
  static String appOpenPath(String brainId, String appId) =>
      appRequestPath(brainId, appId);

  /// The brain-scoped key of one installed app; its neurons live under it.
  static String appKey(String brainId, String appId) => '$brainId/apps/$appId';

  /// The live signals of `types` (simple type names, e.g. DeploymentChanged) on the brain's
  /// `/live` feed. Each source's retained value arrives first, so a late listener starts current.
  /// A reader that falls behind gets `resync`: the feed reconnects and replays, so listeners read again.
  /// The pause before reconnecting after a resync, doubling up to [maxResyncPause].
  Duration firstResyncPause = const Duration(seconds: 1);
  Duration maxResyncPause = const Duration(seconds: 30);

  Stream<LiveEnvelope> watchLive(String brainId, List<String> types) async* {
    final path =
        '/brains/${Uri.encodeComponent(brainId)}/live?${types.map((type) => 'type=${Uri.encodeQueryComponent(type)}').join('&')}';
    var resyncPause = firstResyncPause;
    while (true) {
      var resync = false;
      await for (final envelope in _serverEvents(
        path,
        'Live signals are unavailable.',
      ).map(LiveEnvelope.fromJson)) {
        if (envelope.isResync) {
          resync = true;
          break;
        }
        yield envelope;
      }
      // The server ended a stream this reader fell behind on: reconnecting replays current state.
      if (!resync) return;
      // A server that resyncs on every connect must not spin: wait 1 s, doubling to 30 s.
      await Future<void>.delayed(resyncPause);
      resyncPause = resyncPause * 2 > maxResyncPause
          ? maxResyncPause
          : resyncPause * 2;
    }
  }

  Stream<DeploymentChanged> watchDeployment(String brainId) =>
      watchLive(brainId, ['DeploymentChanged'])
          .where((envelope) => !envelope.isReady)
          .map((envelope) => DeploymentChanged.fromJson(envelope.payload));

  Stream<AppActivity> watchActivity(String brainId) =>
      watchLive(brainId, ['AppActivity'])
          .where((envelope) => !envelope.isReady)
          .map((envelope) => AppActivity.fromJson(envelope.payload));

  /// The brains the signed-in person is in, with their kind.
  Future<List<BrainEntry>> listBrains() async {
    final body = await _tableRequest('GET', '/identity/brains');
    return (body as List)
        .map(
          (item) => BrainEntry.fromJson(Map<String, dynamic>.from(item as Map)),
        )
        .toList();
  }

  /// R126: every circle brain the person is in, each watched on its own `/live` feed, merged. The circles are
  /// listed once per subscription; with none the stream ends, and a listener that resubscribes lists again.
  Stream<CircleActivity> watchCircleActivity() {
    late StreamController<CircleActivity> controller;
    final subscriptions = <StreamSubscription<CircleActivity>>[];
    controller = StreamController<CircleActivity>(
      onListen: () async {
        try {
          final circles = (await listBrains())
              .where((brain) => brain.isCircle)
              .map((brain) => brain.id)
              .toList();
          if (circles.isEmpty) {
            await controller.close();
            return;
          }
          var open = circles.length;
          for (final circle in circles) {
            subscriptions.add(
              watchLive(circle, ['CircleSpoke', 'CircleChanged'])
                  .map(
                    (envelope) => CircleActivity.fromEnvelope(circle, envelope),
                  )
                  .listen(
                    controller.add,
                    onError: controller.addError,
                    onDone: () {
                      if (--open == 0) controller.close();
                    },
                  ),
            );
          }
        } catch (error, stack) {
          controller.addError(error, stack);
          await controller.close();
        }
      },
      onCancel: () async {
        for (final subscription in subscriptions) {
          await subscription.cancel();
        }
      },
    );
    return controller.stream;
  }

  Stream<Map<String, dynamic>> _serverEvents(String path, String unavailable) {
    final abort = Completer<void>();
    StreamSubscription<String>? incoming;
    late StreamController<Map<String, dynamic>> controller;
    controller = StreamController<Map<String, dynamic>>(
      onListen: () async {
        try {
          final request = http.AbortableRequest(
            'GET',
            baseUri.resolve(path),
            abortTrigger: abort.future,
          )..headers['accept'] = 'text/event-stream';
          final response = await _http.send(request);
          if (abort.isCompleted || response.statusCode != 200) {
            await response.stream.listen(null).cancel();
            if (!abort.isCompleted) {
              throw TableRequestException(response.statusCode, unavailable);
            }
            return;
          }
          incoming = response.stream
              .transform(utf8.decoder)
              .transform(const LineSplitter())
              .listen(
                (line) {
                  if (!line.startsWith('data:')) return;
                  try {
                    controller.add(
                      jsonDecode(line.substring(5).trimLeft())
                          as Map<String, dynamic>,
                    );
                  } catch (error, stack) {
                    controller.addError(error, stack);
                  }
                },
                onError: (Object error, StackTrace stack) {
                  if (!abort.isCompleted) controller.addError(error, stack);
                },
                onDone: controller.close,
              );
        } catch (error, stack) {
          if (!abort.isCompleted) {
            controller.addError(error, stack);
            unawaited(controller.close());
          }
        }
      },
      onCancel: () async {
        if (!abort.isCompleted) abort.complete();
        await incoming?.cancel();
      },
    );
    return controller.stream;
  }

  Future<Object?> _tableRequest(
    String method,
    String path, {
    Map<String, Object?>? body,
    Duration timeout = const Duration(seconds: 40),
    Future<void>? cancelled,
  }) async {
    try {
      final response = await _request(
        method,
        path,
        body: body,
        timeout: timeout,
        cancelled: cancelled,
      );
      return response.body.isEmpty ? null : jsonDecode(response.body);
    } on UiRequestException catch (error) {
      throw TableRequestException(
        error.statusCode,
        error.error ?? error.detail ?? error.body,
      );
    }
  }

  /// The brain's scenes over this client's authenticated session.
  SceneClient scenes(String workspace) =>
      SceneClient(_http, baseUri, workspace);

  Future<http.Response> _request(
    String method,
    String path, {
    Map<String, Object?>? body,
    Duration? timeout,
    Future<void>? cancelled,
  }) async {
    final abort = timeout == null && cancelled == null
        ? null
        : Completer<void>();
    final request = abort == null
        ? http.Request(method, baseUri.resolve(path))
        : http.AbortableRequest(
            method,
            baseUri.resolve(path),
            abortTrigger: cancelled == null
                ? abort.future
                : Future.any([abort.future, cancelled]),
          );
    if (body != null) {
      request.headers['content-type'] = 'application/json';
      request.body = jsonEncode(body);
    }
    final operation = _http.send(request).then(http.Response.fromStream);
    final response = timeout == null
        ? await operation
        : await operation.timeout(
            timeout,
            onTimeout: () {
              // Stop the actual request, so timed-out graph polls cannot accumulate
              // network work while the store schedules a retry.
              abort!.complete();
              throw TimeoutException('$method $path timed out', timeout);
            },
          );
    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw UiRequestException(
        method,
        path,
        response.statusCode,
        response.body,
      );
    }
    return response;
  }

  void close() {
    if (_ownsClient) {
      _http.close();
    }
    for (final mux in _watches.values) {
      unawaited(mux.close());
    }
    _watches.clear();
    unawaited(_link?.shutdown() ?? Future.value());
    _link = null;
  }

  Future<dynamic> jsonRequest(
    String method,
    String path, [
    Object? body,
  ]) async {
    // Creating or authoring an app is one model inference call; everything else on the
    // apps surface (implement) answers immediately with 202.
    final authoring =
        method == 'POST' &&
        (path.endsWith('/apps') || path.endsWith('/author'));
    final response = await _request(
      method,
      path,
      body: body == null ? null : Map<String, Object?>.from(body as Map),
      timeout: authoring
          ? const Duration(minutes: 3)
          : const Duration(seconds: 30),
    );
    return response.body.isEmpty ? null : jsonDecode(response.body);
  }
}

/// Includes status without requiring callers to parse server error text.
final class UiRequestException extends StateError {
  factory UiRequestException(
    String method,
    String path,
    int statusCode,
    String body,
  ) {
    final decoded = _decodeMap(body);
    return UiRequestException._(
      method,
      path,
      statusCode,
      body,
      detail: _stringField(decoded, 'detail'),
      error: _stringField(decoded, 'error'),
      status: _stringField(decoded, 'status'),
    );
  }

  UiRequestException._(
    String method,
    String path,
    this.statusCode,
    this.body, {
    required this.detail,
    required this.error,
    required this.status,
  }) : super('$method $path failed: $statusCode $body');

  final int statusCode;
  final String body;
  final String? detail;

  /// The answer's own `error` and `status` strings, when it is a JSON object that carries them.
  final String? error, status;

  static Map<Object?, Object?>? _decodeMap(String body) {
    if (body.isEmpty) return null;
    try {
      final value = jsonDecode(body);
      return value is Map ? value : null;
    } on FormatException {
      // Non-JSON failures keep their existing diagnostic text.
      return null;
    }
  }

  static String? _stringField(Map<Object?, Object?>? map, String name) {
    final value = map?[name];
    return value is String ? value : null;
  }

  @override
  String toString() => detail ?? super.toString();
}

final class CSharpFileRequestException implements Exception {
  const CSharpFileRequestException(this.statusCode, this.message);
  final int statusCode;
  final String message;
  @override
  String toString() => 'C# file request failed ($statusCode): $message';
}
