import 'package:web_socket_channel/io.dart';
import 'package:web_socket_channel/web_socket_channel.dart';

/// Opens a `dart:io` WebSocket channel with optional session/auth headers.
Future<WebSocketChannel> connectTalkWebSocket(
  Uri uri, {
  Map<String, String> headers = const {},
}) async {
  final channel = IOWebSocketChannel.connect(
    uri,
    headers: headers.isEmpty ? null : headers,
  );
  await channel.ready;
  return channel;
}
