import 'dart:async';
import 'dart:convert';
import 'dart:developer' as developer;
import 'dart:typed_data';

import 'package:http/http.dart' as http;

import 'basic_credentials.dart';
import 'cookie_http_client.dart';
import 'session_http_client_io.dart'
    if (dart.library.js_interop) 'session_http_client_web.dart'
    as transport;
import 'host_environment.dart';
import 'journal_tail_client.dart';
import 'models/app_manifest.dart';
import 'models/installed_app.dart';
import 'models/journal_models.dart';
import 'models/cluster.dart';
import 'models/brain_models.dart';
import 'models/compute_usage.dart';
import 'neuron_channel.dart';
import 'neuron_watch_mux.dart';
import 'node_contracts.dart';
import 'scene_client.dart';
import 'models/table_models.dart';
import 'models/workspace_models.dart';

final class IntoChatOSUiClient {
  /// Gated on the kernel; 404 when the kernel runs ungated.
  static const authCheckPath = '/auth/check';
  static const localPresencePath = '/auth/local-presence';

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
       _credentials = credentials;

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

  /// Exchanges a web `#presence=<token>` fragment proof for an `HttpOnly; SameSite=Strict`
  /// `IntoChatPresence` cookie before probing the kernel session.
  Future<void> exchangeLocalPresence(String token) async {
    if (token.isEmpty) return;
    await _request(
      'POST',
      localPresencePath,
      body: {'token': token},
      timeout: const Duration(seconds: 10),
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
  final String? _presenceToken;

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
      if (_presenceToken case final String token when token.isNotEmpty)
        'x-intochat-presence': token,
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
    final contract = NodeContracts.of(friendSlot ? 'friends.space' : kind);
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

  Future<List<TableSummary>> listTables(String workspace) async {
    final body = await _tableRequest(
      'GET',
      '/brains/${Uri.encodeComponent(workspace)}/ui/tables',
    );
    return (body as List)
        .map(
          (item) =>
              TableSummary.fromJson(Map<String, dynamic>.from(item as Map)),
        )
        .toList();
  }

  /// This brain's apps, with where each installed copy came from and whether an update is offered.
  Future<List<InstalledAppSummary>> listInstalledApps(String workspace) async {
    final body = await _tableRequest(
      'GET',
      '/brains/${Uri.encodeComponent(workspace)}/apps',
    );
    return (body as List)
        .map(
          (item) => InstalledAppSummary.fromJson(
            Map<String, dynamic>.from(item as Map),
          ),
        )
        .toList();
  }

  static String appRequestPath(String brainId, String appId) =>
      '/brains/${Uri.encodeComponent(brainId)}/apps/${Uri.encodeComponent(appId)}/request';

  /// The manifest an app declares, read from the same view its detail page uses.
  Future<AppManifest> readAppManifest(String brainId, String appId) async {
    final body = await jsonRequest(
      'GET',
      '/brains/${Uri.encodeComponent(brainId)}/apps/${Uri.encodeComponent(appId)}',
    );
    return AppManifest.fromJson(
      Map<String, dynamic>.from((body as Map)['manifest'] as Map),
    );
  }

  /// The route that publishes an open request; the app answers through neurons, never here.
  static String appOpenPath(String brainId, String appId) =>
      appRequestPath(brainId, appId);

  /// The brain-scoped key of one installed app; its neurons live under it.
  static String appKey(String brainId, String appId) => '$brainId/apps/$appId';

  /// Publishes a request to an app; the app answers through neurons, never through this call.
  Future<String> requestApp(
    String brainId,
    String appId,
    String operation,
    String input,
  ) async {
    final body = await jsonRequest('POST', appRequestPath(brainId, appId), {
      'operation': operation,
      'input': input,
    });
    return (body as Map)['requestId'] as String;
  }

  /// Raw bytes of a served file such as an app icon; null when the server has none.
  Future<Uint8List?> readBytes(String path) async {
    try {
      final response = await _request(
        'GET',
        path,
        timeout: const Duration(seconds: 30),
      );
      return response.bodyBytes;
    } on UiRequestException catch (error) {
      if (error.statusCode == 404) return null;
      rethrow;
    }
  }

  Future<TableSnapshot> readTable(
    String workspace,
    String id, {
    int offset = 0,
    int limit = 50,
  }) async => TableSnapshot.fromJson(
    Map<String, dynamic>.from(
      await _tableRequest(
        'GET',
        '/brains/${Uri.encodeComponent(workspace)}/ui/tables/${Uri.encodeComponent(id)}?offset=$offset&limit=$limit',
      ) as Map,
    ),
  );

  Future<TableSnapshot> updateTableView(
    String workspace,
    String id,
    TableViewUpdate update,
  ) async => TableSnapshot.fromJson(
    Map<String, dynamic>.from(
      await _tableRequest(
        'POST',
        '/brains/${Uri.encodeComponent(workspace)}/ui/tables/${Uri.encodeComponent(id)}/view',
        body: update.toJson(),
      ) as Map,
    ),
  );

  Future<WorkspaceSnapshot> readWorkspace(
    String workspaceId, {
    Future<void>? cancelled,
  }) async => WorkspaceSnapshot.fromJson(
    Map<String, dynamic>.from(
      await _tableRequest(
        'GET',
        '/brains/${Uri.encodeComponent(workspaceId)}',
        cancelled: cancelled,
      ) as Map,
    ),
  );

  Future<WorkspaceSnapshot> showNeuron(
    String workspace,
    String key,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'show', operationId, expectedRevision, key);
  Future<WorkspaceSnapshot> backNeuron(
    String workspace,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'back', operationId, expectedRevision);
  Future<WorkspaceSnapshot> homeNeuron(
    String workspace,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'home', operationId, expectedRevision);
  Future<WorkspaceSnapshot> closeNeuron(
    String workspace,
    String key,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'close', operationId, expectedRevision, key);

  Future<WorkspaceSnapshot> _navigateNeuron(
    String workspace,
    String command,
    String operationId,
    int expectedRevision, [
    String? key,
  ]) async => WorkspaceSnapshot.fromJson(
    Map<String, dynamic>.from(
      await _tableRequest(
        'POST',
        '/brains/${Uri.encodeComponent(workspace)}/$command',
        body: {
          'app': ?key,
          'operationId': operationId,
          'expectedRevision': expectedRevision,
        },
      ) as Map,
    ),
  );

  Future<void> reportProblem({
    required String workspaceId,
    required String intentId,
    required String message,
  }) async {
    await _tableRequest(
      'POST',
      '/brains/${Uri.encodeComponent(workspaceId)}/reports',
      body: {'intentId': intentId, 'message': message},
    );
  }

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

  /// The brain's neurons as snapshots: the whole brain with [brainId], or one chat's view with [chatName].
  Stream<BrainSnapshot> watchBrain({String? chatName, String? brainId}) {
    if ((chatName == null) == (brainId == null)) {
      throw ArgumentError('Name a chat or a brain, not both.');
    }
    final path = brainId != null
        ? '/brains/${Uri.encodeComponent(brainId)}/brain/events'
        : '/chats/${Uri.encodeComponent(chatName!)}/brain/events';
    final abort = Completer<void>();
    late StreamController<BrainSnapshot> controller;
    controller = StreamController<BrainSnapshot>(
      onListen: () async {
        try {
          final request = http.AbortableRequest(
            'GET',
            baseUri.resolve(path),
            abortTrigger: abort.future,
          )..headers['accept'] = 'text/event-stream';
          final response = await _http.send(request);
          if (response.statusCode != 200) {
            throw StateError(
              'Brain stream unavailable (${response.statusCode}).',
            );
          }
          String? event;
          final data = <String>[];
          await for (final line
              in response.stream
                  .transform(utf8.decoder)
                  .transform(const LineSplitter())) {
            if (abort.isCompleted) break;
            if (line.startsWith('event:')) event = line.substring(6).trim();
            if (line.startsWith('data:')) {
              data.add(line.substring(5).trimLeft());
            }
            if (line.isEmpty) {
              if (event == 'brain-snapshot' && data.isNotEmpty) {
                controller.add(
                  BrainSnapshot.fromJson(
                    jsonDecode(data.join('\n')) as Map<String, dynamic>,
                  ),
                );
              }
              event = null;
              data.clear();
            }
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

  /// The brain's journal as batches of signals after [since] (a sequence number), each new one as it is journaled; the
  /// stream reopens from the last sequence seen when the connection drops and fails with [JournalTailRefused] when refused.
  Stream<List<JournalTailSignal>> watchJournal(
    String brainId, {
    int since = 0,
  }) => JournalTailClient(
    baseUri: baseUri,
    httpClient: _http,
  ).watch(brainId, since: since);

  /// A unit's current state record as JSON, or null when [alias] has none saved (or is not a loaded unit).
  Future<Map<String, dynamic>?> readUnitState(
    String brainId,
    String alias,
  ) async => _unitRead(brainId, 'state', alias);

  /// A unit's published view, or null before its first view (or when [alias] is not a loaded unit).
  Future<UnitView?> readUnitView(String brainId, String alias) async {
    final json = await _unitRead(brainId, 'view', alias);
    return json == null ? null : UnitView.fromJson(json);
  }

  Future<Map<String, dynamic>?> _unitRead(
    String brainId,
    String what,
    String alias,
  ) async {
    try {
      final response = await _request(
        'GET',
        '/brains/${Uri.encodeComponent(brainId)}/$what?alias=${Uri.encodeQueryComponent(alias)}',
      );
      return (jsonDecode(response.body) as Map).cast<String, dynamic>();
    } on UiRequestException catch (error) {
      if (error.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Spend and balance for one brain, grouped by [by]; rows are most expensive first.
  Future<ComputeSummary> readCompute(
    String brainId, {
    String by = 'app',
    DateTime? since,
  }) async {
    final query = Uri(
      queryParameters: {'by': by, 'since': ?since?.toUtc().toIso8601String()},
    ).query;
    return ComputeSummary.fromJson(
      Map<String, dynamic>.from(
        await _tableRequest(
          'GET',
          '/brains/${Uri.encodeComponent(brainId)}/compute?$query',
        ) as Map,
      ),
    );
  }

  /// The most recent individual costs for one brain, newest [take] first.
  Future<List<ComputeCost>> readComputeRecent(
    String brainId, {
    int take = 50,
  }) async {
    final body = await _tableRequest(
      'GET',
      '/brains/${Uri.encodeComponent(brainId)}/compute/recent?take=$take',
    );
    return [
      for (final item in body as List)
        ComputeCost.fromJson(Map<String, dynamic>.from(item as Map)),
    ];
  }

  Future<ComputeTracePage> readComputeTrace(
    String brainId,
    String traceId, {
    int take = 100,
    String? continuationToken,
  }) async {
    if (take < 1 || take > 500) {
      throw ArgumentError.value(take, 'take', 'Must be between 1 and 500.');
    }
    final query = Uri(
      queryParameters: {
        'take': '$take',
        'continuationToken': ?continuationToken,
      },
    ).query;
    return ComputeTracePage.fromJson(
      Map<String, dynamic>.from(
        await _tableRequest(
          'GET',
          '/brains/${Uri.encodeComponent(brainId)}/compute/traces/${Uri.encodeComponent(traceId)}?$query',
        ) as Map,
      ),
    );
  }

  Future<void> postUi(String path, {Map<String, Object?>? body}) async {
    await _request(
      'POST',
      path,
      body: body,
      timeout: const Duration(seconds: 10),
    );
  }

  Future<BrainSnapshot> readBrain({required String chatName}) async {
    final response = await _request(
      'GET',
      '/chats/${Uri.encodeComponent(chatName)}/brain',
      timeout: const Duration(seconds: 10),
    );
    return BrainSnapshot.fromJson(
      jsonDecode(response.body) as Map<String, dynamic>,
    );
  }

  /// The brain's scenes over this client's authenticated session.
  SceneClient scenes(String workspace) =>
      SceneClient(_http, baseUri, workspace);

  /// Transcribes recorded [audio] bytes (`fileName` names the audio format, e.g. `voice.wav`)
  /// through `POST /ai/transcriptions` and returns the recognized text.
  Future<String> transcribe(Uint8List audio, String fileName) async {
    final trimmed = fileName.trim();
    final safeName = trimmed.isEmpty ? 'voice.wav' : trimmed;
    final path = Uri(
      path: '/ai/transcriptions',
      queryParameters: {'fileName': safeName},
    ).toString();
    final response = await _request(
      'POST',
      path,
      bodyBytes: audio,
      contentType: _audioContentType(safeName),
      timeout: const Duration(minutes: 5),
    );
    final decoded = response.body.isEmpty
        ? const <String, dynamic>{}
        : jsonDecode(response.body);
    if (decoded is Map && decoded['text'] is String) {
      return decoded['text'] as String;
    }
    throw StateError('Transcription response missing text.');
  }

  static String _audioContentType(String fileName) {
    final lower = fileName.toLowerCase();
    if (lower.endsWith('.wav')) return 'audio/wav';
    if (lower.endsWith('.ogg')) return 'audio/ogg';
    if (lower.endsWith('.opus')) return 'audio/opus';
    if (lower.endsWith('.mp3')) return 'audio/mpeg';
    if (lower.endsWith('.m4a') || lower.endsWith('.mp4')) return 'audio/mp4';
    if (lower.endsWith('.webm')) return 'audio/webm';
    if (lower.endsWith('.flac')) return 'audio/flac';
    return 'application/octet-stream';
  }

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

  Future<Map<String, dynamic>> appRequest(
    String workspace,
    String path, {
    Map<String, Object?>? body,
  }) async {
    final response = await _request(
      body == null ? 'GET' : 'POST',
      '/brains/${Uri.encodeComponent(workspace)}/apps/$path',
      body: body,
      timeout: const Duration(seconds: 30),
    );
    return Map<String, dynamic>.from(jsonDecode(response.body) as Map);
  }

  /// Posts one UI event from a [NodeContext]: an `act` goes to
  /// `POST /brains/{workspace}/act` (AppUiEndpoints.ActRequest), every other
  /// kind to `POST /brains/{workspace}/apps/event`.
  Future<Map<String, dynamic>> uiEvent(
    String workspace,
    Map<String, Object?> event,
  ) async {
    if (event['kind'] != 'act') {
      return appRequest(workspace, 'event', body: event);
    }
    final response = await _request(
      'POST',
      '/brains/${Uri.encodeComponent(workspace)}/act',
      body: {
        'neuron': event['neuron'],
        'target': event['target'],
        'signal': event['signal'],
        'args': event['args'],
      },
      timeout: const Duration(seconds: 30),
    );
    return Map<String, dynamic>.from(jsonDecode(response.body) as Map);
  }

  Future<Map<String, dynamic>> storeSecret(
    String name,
    String label,
    String value,
  ) async {
    final owner = principalId;
    if (owner == null) {
      throw StateError('Sign in before storing a secret.');
    }
    final response = await _request(
      'POST',
      '/secrets/${Uri.encodeComponent(owner)}',
      body: {'name': name, 'label': label, 'value': value},
      timeout: const Duration(seconds: 30),
    );
    return response.body.isEmpty
        ? <String, dynamic>{}
        : Map<String, dynamic>.from(jsonDecode(response.body) as Map);
  }

  Future<Object?> connectorRequest(
    String workspaceId,
    String path, {
    Map<String, Object?>? body,
  }) {
    if (path.isNotEmpty) {
      throw ArgumentError.value(
        path,
        'path',
        'The connector route is the catalog only.',
      );
    }
    return jsonRequest('GET', '/connectors');
  }

  Future<Object?> workspaceConnectionsRequest(
    String workspaceId,
    String path, {
    Map<String, Object?>? body,
  }) => jsonRequest(
    body == null ? 'GET' : 'POST',
    '/brains/${Uri.encodeComponent(workspaceId)}/connections${path.isEmpty ? '' : '/$path'}',
    body,
  );

  Future<List<Map<String, dynamic>>> listWorkspaceConnections(
    String workspaceId,
  ) async {
    final result = await workspaceConnectionsRequest(workspaceId, '');
    return [
      for (final item in (result as List? ?? const []))
        Map<String, dynamic>.from(item as Map),
    ];
  }

  Future<Map<String, dynamic>> connectWorkspaceSecret(
    String workspaceId,
    String connectionId, {
    required String connectorId,
    required String authMethodId,
    required String value,
  }) async {
    final result = await workspaceConnectionsRequest(
      workspaceId,
      '${Uri.encodeComponent(connectionId)}/connect',
      body: {
        'connectorId': connectorId,
        'authMethodId': authMethodId,
        'value': value,
      },
    );
    return result is Map ? Map<String, dynamic>.from(result) : {};
  }

  Future<Map<String, dynamic>> startWorkspaceConnectionLogin(
    String workspaceId,
    String connectionId, {
    required String connectorId,
    required String authMethodId,
    String? target,
  }) async {
    final result = await workspaceConnectionsRequest(
      workspaceId,
      '${Uri.encodeComponent(connectionId)}/login',
      body: {
        'connectorId': connectorId,
        'authMethodId': authMethodId,
        'target': ?target,
      },
    );
    return result is Map ? Map<String, dynamic>.from(result) : {};
  }

  Future<Map<String, dynamic>> probeWorkspaceConnection(
    String workspaceId,
    String connectionId,
  ) async {
    final result = await workspaceConnectionsRequest(
      workspaceId,
      '${Uri.encodeComponent(connectionId)}/probe',
      body: const {},
    );
    return result is Map ? Map<String, dynamic>.from(result) : {};
  }

  Future<void> disconnectWorkspaceConnection(
    String workspaceId,
    String connectionId,
  ) async {
    await workspaceConnectionsRequest(
      workspaceId,
      '${Uri.encodeComponent(connectionId)}/disconnect',
      body: const {},
    );
  }

  Future<Object?> workspaceWindowsRequest(
    String workspaceId,
    String path, {
    Map<String, Object?>? body,
  }) => jsonRequest(
    body == null ? 'GET' : 'POST',
    '/brains/${Uri.encodeComponent(workspaceId)}/windows/$path',
    body,
  );

  Future<OsNotificationList> listNotifications(
    String brainId, {
    int limit = 50,
    bool unreadOnly = false,
  }) async {
    final response = await _request(
      'GET',
      '/brains/${Uri.encodeComponent(brainId)}/notifications?limit=$limit&unreadOnly=$unreadOnly',
      timeout: const Duration(seconds: 15),
    );
    if (response.body.isEmpty) {
      return const OsNotificationList(items: [], unreadCount: 0);
    }
    return OsNotificationList.fromJson(
      Map<String, dynamic>.from(jsonDecode(response.body) as Map),
    );
  }

  Future<void> markNotificationRead(
    String brainId,
    String notificationId,
  ) async {
    await _request(
      'POST',
      '/brains/${Uri.encodeComponent(brainId)}/notifications/${Uri.encodeComponent(notificationId)}/read',
      body: const {},
      timeout: const Duration(seconds: 15),
    );
  }

  Future<void> markAllNotificationsRead(String brainId) async {
    await _request(
      'POST',
      '/brains/${Uri.encodeComponent(brainId)}/notifications/read-all',
      body: const {},
      timeout: const Duration(seconds: 15),
    );
  }

  Future<void> clearNotifications(String brainId) async {
    await _request(
      'POST',
      '/brains/${Uri.encodeComponent(brainId)}/notifications/clear',
      body: const {},
      timeout: const Duration(seconds: 15),
    );
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

enum OsNotificationPriority {
  low,
  normal,
  high,
  urgent;

  static OsNotificationPriority parse(Object? raw) {
    if (raw is int) {
      return switch (raw) {
        0 => OsNotificationPriority.low,
        2 => OsNotificationPriority.high,
        3 => OsNotificationPriority.urgent,
        _ => OsNotificationPriority.normal,
      };
    }
    if (raw is String) {
      return switch (raw.toLowerCase()) {
        'low' => OsNotificationPriority.low,
        'high' => OsNotificationPriority.high,
        'urgent' => OsNotificationPriority.urgent,
        _ => OsNotificationPriority.normal,
      };
    }
    return OsNotificationPriority.normal;
  }
}

final class OsNotificationItem {
  const OsNotificationItem({
    required this.id,
    required this.brainId,
    required this.sourceApp,
    required this.title,
    required this.body,
    required this.priority,
    this.category,
    this.actionUrl,
    required this.read,
    required this.createdUtc,
    this.readUtc,
  });

  final String id;
  final String brainId;
  final String sourceApp;
  final String title;
  final String body;
  final OsNotificationPriority priority;
  final String? category;
  final String? actionUrl;
  final bool read;
  final DateTime createdUtc;
  final DateTime? readUtc;

  factory OsNotificationItem.fromJson(Map<String, dynamic> json) {
    return OsNotificationItem(
      id: (json['id'] as String?) ?? '',
      brainId: (json['brainId'] as String?) ?? '',
      sourceApp: (json['sourceApp'] as String?) ?? 'os',
      title: (json['title'] as String?) ?? '',
      body: (json['body'] as String?) ?? '',
      priority: OsNotificationPriority.parse(json['priority']),
      category: json['category'] as String?,
      actionUrl: json['actionUrl'] as String?,
      read: json['read'] == true,
      createdUtc:
          DateTime.tryParse((json['createdUtc'] as String?) ?? '') ??
          DateTime.now().toUtc(),
      readUtc: DateTime.tryParse((json['readUtc'] as String?) ?? ''),
    );
  }

  OsNotificationItem copyWith({bool? read, DateTime? readUtc}) {
    return OsNotificationItem(
      id: id,
      brainId: brainId,
      sourceApp: sourceApp,
      title: title,
      body: body,
      priority: priority,
      category: category,
      actionUrl: actionUrl,
      read: read ?? this.read,
      createdUtc: createdUtc,
      readUtc: readUtc ?? this.readUtc,
    );
  }
}

final class OsNotificationList {
  const OsNotificationList({required this.items, required this.unreadCount});

  final List<OsNotificationItem> items;
  final int unreadCount;

  factory OsNotificationList.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? [
            for (final item in rawItems)
              if (item is Map)
                OsNotificationItem.fromJson(Map<String, dynamic>.from(item)),
          ]
        : const <OsNotificationItem>[];
    final unreadRaw = json['unreadCount'];
    final unreadCount = unreadRaw is int
        ? unreadRaw
        : items.where((item) => !item.read).length;
    return OsNotificationList(items: items, unreadCount: unreadCount);
  }
}
