/// Pure helper parsing `#presence=<token>` or `#/route?presence=<token>` and
/// returning the stripped fragment without leaving the token in browser history.
({String token, String remainingHash})? parsePresenceFragment(String rawHash) {
  if (rawHash.isEmpty) return null;
  final body = rawHash.startsWith('#') ? rawHash.substring(1) : rawHash;
  if (body.isEmpty) return null;

  // Case 1: `#presence=<token>` or `#foo=1&presence=<token>` (key=value fragment).
  if (!body.startsWith('/')) {
    final parts = body.split('&');
    String? token;
    final kept = <String>[];
    for (final part in parts) {
      final eq = part.indexOf('=');
      if (eq > 0 && part.substring(0, eq) == 'presence' && token == null) {
        final decoded = Uri.decodeQueryComponent(part.substring(eq + 1));
        if (decoded.isNotEmpty) {
          token = decoded;
          continue;
        }
      }
      if (part.isNotEmpty) kept.add(part);
    }
    if (token == null) return null;
    return (
      token: token,
      remainingHash: kept.isEmpty ? '' : '#${kept.join('&')}',
    );
  }

  // Case 2: `#/open?app=ino&presence=<token>` (route fragment with query).
  final q = body.indexOf('?');
  if (q < 0) return null;
  final routePath = body.substring(0, q);
  final queryParts = body.substring(q + 1).split('&');
  String? token;
  final kept = <String>[];
  for (final part in queryParts) {
    final eq = part.indexOf('=');
    if (eq > 0 && part.substring(0, eq) == 'presence' && token == null) {
      final decoded = Uri.decodeQueryComponent(part.substring(eq + 1));
      if (decoded.isNotEmpty) {
        token = decoded;
        continue;
      }
    }
    if (part.isNotEmpty) kept.add(part);
  }
  if (token == null) return null;
  final cleanRoute = kept.isEmpty ? routePath : '$routePath?${kept.join('&')}';
  return (
    token: token,
    remainingHash: cleanRoute.isEmpty || cleanRoute == '/'
        ? ''
        : '#$cleanRoute',
  );
}
