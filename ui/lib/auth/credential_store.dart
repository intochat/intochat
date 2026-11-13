import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

// Basic credentials are kept in memory for the process lifetime; reload
// authentication uses the session cookie.
BasicCredentials? _credentials;

BasicCredentials? readStoredCredentials() => _credentials;

void writeStoredCredentials(BasicCredentials credentials) {
  _credentials = credentials;
}

void clearStoredCredentials() {
  _credentials = null;
}
