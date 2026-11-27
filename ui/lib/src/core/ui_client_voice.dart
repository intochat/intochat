part of 'ui_client.dart';

extension IntoChatOSUiClientVoice on IntoChatOSUiClient {
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

  /// Mints a 30-second, single-use opaque ticket via `POST /ai/talk/ticket`
  /// for authenticating browser WebSocket upgrades without query-string bearer tokens.
  Future<String> mintTalkTicket(String conversation) async {
    final trimmed = conversation.trim();
    if (trimmed.isEmpty) {
      throw ArgumentError.value(
        conversation,
        'conversation',
        'Conversation id must not be empty.',
      );
    }
    final response = await _request(
      'POST',
      '/ai/talk/ticket',
      body: {'conversation': trimmed},
      timeout: const Duration(seconds: 15),
    );
    final decoded = response.body.isEmpty
        ? const <String, dynamic>{}
        : jsonDecode(response.body);
    if (decoded is Map &&
        decoded['ticket'] is String &&
        (decoded['ticket'] as String).isNotEmpty) {
      return decoded['ticket'] as String;
    }
    throw StateError('Talk ticket response missing ticket.');
  }

  /// Opens a full-duplex voice-mode session over `GET /ai/talk?conversation={id}`.
  ///
  /// On Web, browser WebSockets drop custom `Authorization` / `X-IntoChat-Presence`
  /// headers; when header credentials are present (or [useWebTicket] is true),
  /// this mints a 30-second single-use opaque ticket via `POST /ai/talk/ticket`
  /// (zero query-string bearer tokens). Cookie-only web sessions also authenticate
  /// via the browser's HttpOnly `SameSite` session cookie.
  Future<TalkSocket> talk(
    String conversation, {
    bool useWebTicket = const bool.fromEnvironment('dart.library.js_interop'),
  }) async {
    final headers = <String, String>{
      if (_credentials?.authorizationHeader case final String auth
          when auth.isNotEmpty)
        'authorization': auth,
      if (_presenceToken case final String token when token.isNotEmpty)
        'x-intochat-presence': token,
      if (_http.cookies.isNotEmpty)
        'cookie': _http.cookies.entries
            .map((entry) => '${entry.key}=${entry.value}')
            .join('; '),
    };

    String? ticket;
    if (useWebTicket) {
      ticket = await mintTalkTicket(conversation);
    }

    final uri = _talkUri(baseUri, conversation, ticket: ticket);
    return TalkSocket.connect(uri, headers: useWebTicket ? const {} : headers);
  }

  /// Uploads a validated attachment or voice-note clip via `POST /ai/attachments`.
  Future<AttachmentNode> uploadAttachment(
    Uint8List bytes,
    String fileName, {
    required String mediaType,
    bool isVoiceNote = false,
    int? durationMs,
    String? conversation,
    bool transcribe = true,
  }) async {
    final query = <String, String>{
      'fileName': fileName.trim(),
      if (isVoiceNote) 'kind': 'voice-note',
      if (durationMs != null) 'durationMs': '$durationMs',
      if (conversation != null && conversation.trim().isNotEmpty)
        'conversation': conversation.trim(),
      if (isVoiceNote && !transcribe) 'transcribe': 'false',
    };
    final path = Uri(
      path: '/ai/attachments',
      queryParameters: query,
    ).toString();
    final response = await _request(
      'POST',
      path,
      bodyBytes: bytes,
      contentType: mediaType,
      timeout: const Duration(minutes: 3),
    );
    final decoded = jsonDecode(response.body);
    if (decoded is Map) {
      return AttachmentNode.fromJson(Map<String, dynamic>.from(decoded));
    }
    throw StateError('Attachment upload response was not a JSON object.');
  }

  /// Downloads brain-owned attachment bytes via an authenticated `GET /ai/attachments/{id}`.
  Future<Uint8List> fetchAttachmentBytes(String url) async {
    final response = await _request(
      'GET',
      url,
      timeout: const Duration(minutes: 2),
    );
    return Uint8List.fromList(response.bodyBytes);
  }

  /// Synthesizes [text] into playable audio bytes (WAV) through `POST /ai/speech`.
  Future<Uint8List> speak(String text) async {
    final trimmed = text.trim();
    if (trimmed.isEmpty) {
      throw ArgumentError.value(text, 'text', 'Speech text must not be empty.');
    }
    final response = await _request(
      'POST',
      '/ai/speech',
      body: {'text': trimmed},
      timeout: const Duration(minutes: 2),
    );
    return Uint8List.fromList(response.bodyBytes);
  }
}

Uri _talkUri(Uri baseUri, String conversation, {String? ticket}) {
  final trimmed = conversation.trim();
  if (trimmed.isEmpty) {
    throw ArgumentError.value(
      conversation,
      'conversation',
      'Conversation id must not be empty.',
    );
  }
  final query = <String, String>{
    'conversation': trimmed,
    if (ticket != null && ticket.trim().isNotEmpty) 'ticket': ticket.trim(),
  };
  final httpUri = baseUri.resolve(
    Uri(path: '/ai/talk', queryParameters: query).toString(),
  );
  return httpUri.replace(scheme: httpUri.scheme == 'https' ? 'wss' : 'ws');
}

String _audioContentType(String fileName) {
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
