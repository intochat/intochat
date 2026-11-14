import 'dart:js_interop';

import 'package:web/web.dart' as web;

import 'presence_fragment.dart';

Uri? sameOriginUiBase() {
  final origin = Uri.base.origin;
  if (origin.isEmpty) {
    return null;
  }
  return Uri.parse('$origin/');
}

/// A per-session kernel URL carried in the page address: the test harness and
/// multi-host setups pass it so bundles stay byte-identical and hosts never
/// share a served config file.
String? queryUiBase() {
  final value = Uri.base.queryParameters['uiBase'];
  return value == null || value.isEmpty ? null : value;
}

const _storageKey = 'intochat.os.uiBase';

@JS('localStorage.getItem')
external JSString? _getItem(JSString key);

@JS('localStorage.setItem')
external void _setItem(JSString key, JSString value);

/// The kernel URL this origin was last pointed at (see rememberedUiBase).
/// Storage can be unavailable (private windows, blocked site data); that is
/// the same as nothing remembered.
String? storedUiBase() {
  try {
    return _getItem(_storageKey.toJS)?.toDart;
  } catch (_) {
    return null;
  }
}

void storeUiBase(String value) {
  try {
    _setItem(_storageKey.toJS, value.toJS);
  } catch (_) {
    // Not remembered; the page address still applies to this session.
  }
}

String? _consumedWebPresenceToken;
bool _inspectedWebPresenceHash = false;

/// Extracts `#presence=<token>` from `window.location.hash` once at startup and
/// immediately strips it from the browser address bar via `history.replaceState`.
String? consumeWebPresenceToken() {
  if (_inspectedWebPresenceHash) {
    final token = _consumedWebPresenceToken;
    _consumedWebPresenceToken = null;
    return token;
  }
  _inspectedWebPresenceHash = true;
  try {
    final rawHash = web.window.location.hash;
    final parsed = parsePresenceFragment(rawHash);
    if (parsed == null) return null;
    final pathname = web.window.location.pathname;
    final search = web.window.location.search;
    final cleanUrl = '$pathname$search${parsed.remainingHash}';
    web.window.history.replaceState(null, '', cleanUrl);
    return parsed.token;
  } catch (_) {
    return null;
  }
}
