import 'package:web/web.dart' as web;

import 'local_account.dart';

Future<LocalAccount?> readLocalAccount(Uri server) async => null;

void openAuthUrl(Uri url) {
  web.window.location.assign(url.toString());
}
