part of 'ui_client.dart';

extension IntoChatOSUiClientAuth on IntoChatOSUiClient {
  /// Exchanges a web `#presence=<token>` fragment proof for an `HttpOnly; SameSite=Strict`
  /// `IntoChatPresence` cookie before probing the kernel session.
  Future<void> exchangeLocalPresence(String token) async {
    if (token.isEmpty) return;
    await _request(
      'POST',
      IntoChatOSUiClient.localPresencePath,
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
    final uri = baseUri.resolve(IntoChatOSUiClient.authCheckPath);
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

  /// True when the active session is an ephemeral / guest brain, either explicitly flagged
  /// by the session payload, stamped as the `guest` app, or carrying the reserved `try-`
  /// guest principal/brain prefix.
  bool get isGuestSession =>
      isEphemeralSession ||
      sessionAppId == 'guest' ||
      (principalId?.startsWith('try-') ?? false) ||
      (defaultBrainId?.startsWith('try-') ?? false);

  /// Remaining lifetime of the guest session when the server reported an expiration time.
  Duration? get guestRemaining {
    final expires = guestExpiresAt;
    if (expires == null) return null;
    final diff = expires.difference(DateTime.now().toUtc());
    return diff.isNegative ? Duration.zero : diff;
  }

  void _useSession(Map<String, dynamic> session) {
    principalId = session['principalId'] as String;
    accountId = session['accountId'] as String;
    defaultBrainId = session['brainId'] as String;
    sessionAppId = session['appId'] as String?;
    isEphemeralSession =
        session['isGuest'] == true || session['isEphemeral'] == true;
    guestExpiresAt = _parseGuestExpiry(session);
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

  /// The brains the signed-in person is in, with their kind.
  Future<List<BrainEntry>> listBrains() async {
    final body = await _tableRequest('GET', '/identity/brains');
    return (body as List)
        .map(
          (item) => BrainEntry.fromJson(Map<String, dynamic>.from(item as Map)),
        )
        .toList();
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

  /// Stores [value] as the signed-in person's secret [name] and returns only
  /// its reference (`secret://{owner}/{name}`), which a form submits.
  Future<String> storeSecretReference(
    String name,
    String label,
    String value,
  ) async {
    final secret = await storeSecret(name, label, value);
    final reference = secret['reference'];
    if (reference is! String || reference.isEmpty) {
      throw StateError('The vault returned no secret reference.');
    }
    return reference;
  }

  /// Mints a one-time SSO launch ticket for an internal WebView route on this
  /// Kernel origin (e.g. `/orleans/`, `/devui/`, `/widgetbook/`).
  ///
  /// Returns the relative enter URL (e.g. `/auth/webview-enter?ticket=...`),
  /// or `null` when the endpoint is unavailable or the request fails.
  Future<String?> mintWebViewTicket(String targetPath) async {
    final trimmed = targetPath.trim();
    if (!trimmed.startsWith('/') ||
        trimmed.startsWith('//') ||
        trimmed.startsWith(r'/\')) {
      return null;
    }
    try {
      final response = await _http
          .post(
            baseUri.resolve(IntoChatOSUiClient.webViewTicketPath),
            headers: const {'content-type': 'application/json'},
            body: jsonEncode({'target': trimmed}),
          )
          .timeout(const Duration(seconds: 5));
      if (response.statusCode != 200) {
        return null;
      }
      final decoded = jsonDecode(response.body);
      if (decoded is Map<String, dynamic>) {
        final enterUrl = decoded['enterUrl'];
        if (enterUrl is String &&
            enterUrl.startsWith('/') &&
            !enterUrl.startsWith('//')) {
          return enterUrl;
        }
      }
      return null;
    } catch (_) {
      return null;
    }
  }
}

DateTime? _parseGuestExpiry(Map<String, dynamic> session) {
  final expiresAt = session['expiresAt'];
  if (expiresAt is String && expiresAt.isNotEmpty) {
    return DateTime.tryParse(expiresAt)?.toUtc();
  }
  if (expiresAt is num) {
    return DateTime.fromMillisecondsSinceEpoch(
      (expiresAt * 1000).round(),
      isUtc: true,
    );
  }
  final remainingSeconds = session['remainingSeconds'];
  if (remainingSeconds is num) {
    return DateTime.now().toUtc().add(
      Duration(seconds: remainingSeconds.round()),
    );
  }
  return null;
}
