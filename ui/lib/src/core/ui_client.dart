import 'dart:async';
import 'dart:convert';
import 'dart:developer' as developer;
import 'dart:typed_data';

import 'package:http/http.dart' as http;

import '../widgets/voice/talk_socket.dart';
import 'basic_credentials.dart';
import 'cookie_http_client.dart';
import 'host_environment.dart';
import 'journal_tail_client.dart';
import 'models/cluster.dart';
import 'models/compute_usage.dart';
import 'models/desktop_projection.dart';
import 'models/installed_app.dart';
import 'models/journal_models.dart';
import 'models/notification_models.dart';
import 'models/table_models.dart';
import 'models/workspace_models.dart';
import 'neuron_channel.dart';
import 'neuron_watch_mux.dart';
import 'node_contracts.dart';
import 'scene_client.dart';
import 'session_http_client_io.dart'
    if (dart.library.js_interop) 'session_http_client_web.dart'
    as transport;

export 'models/notification_models.dart';

part 'ui_client_auth.dart';
part 'ui_client_notifications.dart';
part 'ui_client_voice.dart';
part 'ui_client_workspace.dart';

final class IntoChatOSUiClient {
  /// Gated on the kernel; 404 when the kernel runs ungated.
  static const authCheckPath = '/auth/check';
  static const localPresencePath = '/auth/local-presence';
  static const webViewTicketPath = '/auth/webview-ticket';

  static const screenContract = 'DigitalBrain.IScreen';
  static const screenChangedSignal = 'ViewChanged';

  /// Live updates ride the neuron channel when the client owns its transport (a real kernel),
  /// unless the build is told to keep HTTP polling (`--dart-define=INTOCHATOS_UI_TRANSPORT=http`)
  /// so the comparison can run both. A test that injects its HTTP client keeps the HTTP path,
  /// or supplies a [link] of its own.
  static const liveTransportEnv = 'INTOCHATOS_UI_TRANSPORT';
  static const _liveTransport = String.fromEnvironment(
    liveTransportEnv,
    defaultValue: 'grpc',
  );

  static IntoChatOSUiClient? _activeClient;

  /// The most recently constructed live client, used by embedded WebViews to
  /// exchange the shell's authenticated session for a one-time launch ticket.
  static IntoChatOSUiClient? get activeClient => _activeClient;

  IntoChatOSUiClient({
    required this.baseUri,
    http.Client? httpClient,
    BasicCredentials? credentials,
    String? presenceToken,
  }) : workspaceIdentity = credentials?.username ?? 'local-owner',
       _presenceToken = presenceToken,
       _http = httpClient is CookieHttpClient
           ? httpClient
           : CookieHttpClient(
               httpClient ?? transport.createSessionHttpClient(),
               credentials: credentials,
               presenceToken: presenceToken,
             ),
       _ownsClient = httpClient == null,
       _credentials = credentials {
    _activeClient = this;
  }

  factory IntoChatOSUiClient.fromEnvironment({
    http.Client? httpClient,
    Map<String, String>? processEnvironment,
    BasicCredentials? credentials,
    String? presenceToken,
  }) {
    return IntoChatOSUiClient(
      baseUri: IntoChatOSHostEnv.requireUiBaseUri(
        processEnvironment: processEnvironment,
      ),
      httpClient: httpClient,
      credentials: credentials,
      presenceToken:
          presenceToken ??
          IntoChatOSHostEnv.resolvePresenceToken(
            processEnvironment: processEnvironment,
          ),
    );
  }

  final Uri baseUri;

  /// Non-secret scope for local workspace preferences; never includes credentials.
  String workspaceIdentity;
  String? principalId;
  String? accountId;
  String? defaultBrainId;
  String? sessionAppId;
  bool isEphemeralSession = false;
  DateTime? guestExpiresAt;

  /// True while the client is on a lobby session: an anonymous visitor of a deployment that serves one
  /// (`GET /lobby/session`). The lobby brain is the default brain and its app renders on its own root, so the
  /// shell shows no chrome around it. The flag is generic: any session may carry it.
  bool chromeless = false;

  /// Set by the shell while a lobby session is open; [endLobby] calls it.
  Future<void> Function()? onLobbyEnded;

  final CookieHttpClient _http;
  final bool _ownsClient;
  final BasicCredentials? _credentials;
  final String? _presenceToken;
  bool _pollingAnnounced = false;
  NeuronLink? _link;
  final _watches = <String, NeuronWatchMux>{};

  set link(NeuronLink value) => _link = value;

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

  NeuronLink get channel => _link ??= NeuronChannel(
    baseUri,
    authorization: _credentials?.authorizationHeader,
    sessionHeaders: () => {
      if (_presenceToken case final String token when token.isNotEmpty)
        'x-intochat-presence': token,
      if (_http.cookies.isNotEmpty)
        'cookie': _http.cookies.entries
            .map((entry) => '${entry.key}=${entry.value}')
            .join('; '),
    },
  );

  /// True when [name] explicitly addresses an `app.<name>` screen neuron rather
  /// than an `IView` node path (`{brain}/apps/{name}/view`).
  static bool isExplicitScreenAddress(String name) =>
      _isExplicitScreenAddress(name);

  /// Resolves the canonical `{brainId}/{screenAlias}/default` neuron key of an
  /// `IScreen` from a view or screen address (`{brain}/apps/{app}/view`,
  /// `apps/{app}/view`, `{brain}/app.{app}/default`, `app.{app}`, etc.), or
  /// `null` when [name] does not name an app screen.
  static String? screenNeuronKey(
    String brainId,
    String name, {
    String? alias,
  }) => _screenNeuronKey(brainId, name, alias: alias);

  static String appRequestPath(String brainId, String appId) =>
      '/brains/${Uri.encodeComponent(brainId)}/apps/${Uri.encodeComponent(appId)}/request';

  /// Builds the `ws` / `wss` URI for `GET /ai/talk?conversation={id}` from [baseUri].
  static Uri talkUri(Uri baseUri, String conversation) =>
      _talkUri(baseUri, conversation);

  Future<http.Response> _request(
    String method,
    String path, {
    Map<String, Object?>? body,
    Uint8List? bodyBytes,
    String? contentType,
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
    } else if (bodyBytes != null) {
      request.headers['content-type'] =
          contentType ?? 'application/octet-stream';
      request.bodyBytes = bodyBytes;
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

  void close() {
    if (identical(_activeClient, this)) {
      _activeClient = null;
    }
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
