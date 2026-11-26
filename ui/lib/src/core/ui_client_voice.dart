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

  /// Opens a full-duplex voice-mode session over `GET /ai/talk?conversation={id}`.
  Future<TalkSocket> talk(String conversation) {
    final uri = _talkUri(baseUri, conversation);
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
    return TalkSocket.connect(uri, headers: headers);
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

Uri _talkUri(Uri baseUri, String conversation) {
  final trimmed = conversation.trim();
  if (trimmed.isEmpty) {
    throw ArgumentError.value(
      conversation,
      'conversation',
      'Conversation id must not be empty.',
    );
  }
  final httpUri = baseUri.resolve(
    Uri(
      path: '/ai/talk',
      queryParameters: {'conversation': trimmed},
    ).toString(),
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
