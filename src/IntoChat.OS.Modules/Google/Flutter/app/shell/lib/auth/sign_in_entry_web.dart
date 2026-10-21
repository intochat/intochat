import 'package:web/web.dart' as web;

Future<bool> openSignIn(Uri uri) async {
  web.window.location.assign(uri.toString());
  return true;
}
