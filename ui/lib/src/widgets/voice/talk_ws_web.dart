import 'package:web_socket_channel/web_socket_channel.dart';

/// Opens a browser WebSocket channel (cookies are sent automatically by the browser).
Future<WebSocketChannel> connectTalkWebSocket(
  Uri uri, {
  Map<String, String> headers = const {},
}) async {
  final channel = WebSocketChannel.connect(uri);
  await channel.ready;
  return channel;
}
