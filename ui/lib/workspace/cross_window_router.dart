import 'dart:async';
import 'dart:convert';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

final RegExp _validAppIdPattern = RegExp(r'^[a-z][a-z0-9-]*$');

String? _nonEmptyOrNull(Object? value) {
  if (value is! String) return null;
  final trimmed = value.trim();
  return trimmed.isEmpty ? null : value;
}

/// Normalizes a view transition token, defaulting empty/unknown values to
/// [fallback] (`'fade-through'` for cross-window arrivals).
String normalizeViewTransition(
  String? raw, {
  String fallback = 'fade-through',
}) {
  if (raw == null || raw.trim().isEmpty) return fallback;
  return switch (raw.trim().toLowerCase()) {
    'none' => 'none',
    'fade-through' => 'fade-through',
    'shared-axis-x' => 'shared-axis-x',
    'shared-axis-z' => 'shared-axis-z',
    'camera-fly-to' => 'camera-fly-to',
    _ => 'fade-through',
  };
}

/// Extracts a `{brain}/apps/{app}` screen neuron address from a view or neuron
/// name (returning `null` for `desktop/view` or non-screen legacy `/surface`
/// paths).
String? screenNeuronFromViewName(String? name, {String? brain}) {
  if (name == null) return null;
  final trimmed = name.trim();
  if (trimmed.isEmpty) return null;
  final parts = trimmed.split('/');
  if (parts.length < 3 || parts[1] != 'apps') return null;
  final scope = parts[0];
  if (brain != null && brain.isNotEmpty && scope != brain) return null;
  final app = parts[2].trim();
  if (app.isEmpty || app == 'desktop' || !_validAppIdPattern.hasMatch(app)) {
    return null;
  }
  if (parts.length == 3 || (parts.length == 4 && parts[3] == 'view')) {
    return '$scope/apps/$app';
  }
  return null;
}

/// Parsed `os://window/open?app=<targetApp>&input=...&args=...&transition=...`
/// cross-window navigation payload.
final class OpenWindowRoute {
  OpenWindowRoute({
    required this.app,
    this.input,
    this.argsJson,
    this.rawTransition,
    this.fromNeuron,
    this.section,
  }) : transition = normalizeViewTransition(rawTransition);

  final String app;
  final String? input;
  final String? argsJson;
  final String? rawTransition;
  final String transition;
  final String? fromNeuron;
  final String? section;

  bool get hasExplicitTransition =>
      rawTransition != null && rawTransition!.trim().isNotEmpty;

  /// Whether this route carries arrival state that should dispatch
  /// `ui.navigate-to` (`INavigatedTo<TArgs>`) on the target screen.
  bool get hasNavigationPayload =>
      (input != null && input!.isNotEmpty) ||
      (argsJson != null && argsJson!.isNotEmpty) ||
      hasExplicitTransition;

  static OpenWindowRoute? tryParse(
    String? uriString, {
    String? fallbackFromNeuron,
  }) {
    if (uriString == null || uriString.isEmpty) return null;
    final uri = Uri.tryParse(uriString);
    if (uri == null ||
        uri.scheme != 'os' ||
        uri.host != 'window' ||
        uri.path != '/open' ||
        uri.userInfo.isNotEmpty ||
        uri.hasPort ||
        uri.hasFragment) {
      return null;
    }
    final app = uri.queryParameters['app']?.trim() ?? '';
    if (!_validAppIdPattern.hasMatch(app)) return null;
    return OpenWindowRoute(
      app: app,
      input: _nonEmptyOrNull(uri.queryParameters['input']),
      argsJson: _nonEmptyOrNull(uri.queryParameters['args']),
      rawTransition: _nonEmptyOrNull(uri.queryParameters['transition']),
      fromNeuron:
          _nonEmptyOrNull(uri.queryParameters['from']) ??
          _nonEmptyOrNull(uri.queryParameters['fromNeuron']) ??
          _nonEmptyOrNull(fallbackFromNeuron),
      section: _nonEmptyOrNull(uri.queryParameters['section']),
    );
  }

  OpenWindowRoute mergePending(OpenWindowRoute? stashed) {
    if (stashed == null ||
        stashed.app != app ||
        (stashed.input ?? '') != (input ?? '')) {
      return this;
    }
    return OpenWindowRoute(
      app: app,
      input: input ?? stashed.input,
      argsJson: argsJson ?? stashed.argsJson,
      rawTransition: rawTransition ?? stashed.rawTransition,
      fromNeuron: fromNeuron ?? stashed.fromNeuron,
      section: section ?? stashed.section,
    );
  }
}

/// The route of an `os_uri` window activation: the gated [uriString]
/// (`IntentGate` keeps only the app and its input), merged with the rich
/// `OpenWindow` metadata (`args`, `transition`, `from`) of the node's own
/// [intent] argument when it opens the same app with the same input. The
/// source screen falls back to [fallbackFromNeuron].
OpenWindowRoute? openWindowRouteOf(
  String? uriString, {
  String? intent,
  String? fallbackFromNeuron,
}) {
  final parsed = OpenWindowRoute.tryParse(uriString);
  if (parsed == null) return null;
  final merged = parsed.mergePending(
    OpenWindowRoute.tryParse(intent, fallbackFromNeuron: fallbackFromNeuron),
  );
  if (merged.fromNeuron != null || fallbackFromNeuron == null) {
    return merged;
  }
  return OpenWindowRoute(
    app: merged.app,
    input: merged.input,
    argsJson: merged.argsJson,
    rawTransition: merged.rawTransition,
    fromNeuron: _nonEmptyOrNull(fallbackFromNeuron),
    section: merged.section,
  );
}

ActAction buildNavigateFromActEvent({
  required String fromNeuron,
  required String toNeuron,
  String? route,
  bool force = false,
}) => ActAction(
  neuron: fromNeuron,
  target: '.',
  signal: 'ui.navigate-from',
  args: <String, Object?>{
    'toNeuron': toNeuron,
    if (route != null && route.isNotEmpty) 'route': route,
    'force': force,
  },
);

ActAction buildNavigateToActEvent({
  required String targetNeuron,
  String? fromNeuron,
  String? argsJson,
  String transition = 'fade-through',
  String? input,
  String? correlationId,
}) => ActAction(
  neuron: targetNeuron,
  target: '.',
  signal: 'ui.navigate-to',
  args: <String, Object?>{
    if (argsJson != null && argsJson.isNotEmpty) 'argsJson': argsJson,
    if (fromNeuron != null && fromNeuron.isNotEmpty) 'fromNeuron': fromNeuron,
    'transition': normalizeViewTransition(transition),
    if (correlationId != null && correlationId.isNotEmpty)
      'correlationId': correlationId,
    if (input != null && input.isNotEmpty) 'input': input,
  },
);

/// Extracts a human-readable refusal message ONLY when `/act` rejected
/// `ui.navigate-from` due to a domain `Rule`. Returns `null` when the screen
/// does not handle `ui.navigate-from` or fails for transport/host reasons
/// (fail-open).
String? extractRuleRejectionMessage(Map<String, dynamic>? payload) {
  if (payload == null) return null;
  final reason = payload['reason'];
  if (reason is String && reason.startsWith('Rule:')) {
    final rest = reason.substring('Rule:'.length).trim();
    final colon = rest.indexOf(':');
    if (colon >= 0) {
      final msg = rest.substring(colon + 1).trim();
      if (msg.isNotEmpty) return msg;
    }
    return rest.isEmpty ? 'Navigation blocked by domain rule.' : rest;
  }

  final hasRulesList =
      payload['rules'] is List && (payload['rules'] as List).isNotEmpty;
  final hasRuleString =
      payload['rule'] is String &&
      (payload['rule'] as String).trim().isNotEmpty;
  final explicitRuleReason =
      reason is String && reason.trim().toLowerCase().startsWith('rule');
  if (!hasRulesList && !hasRuleString && !explicitRuleReason) {
    return null;
  }

  if (payload['messages'] is List &&
      (payload['messages'] as List).isNotEmpty &&
      (payload['messages'] as List).first is String) {
    final firstMsg = ((payload['messages'] as List).first as String).trim();
    if (firstMsg.isNotEmpty) return firstMsg;
  }
  for (final key in const ['message', 'detail', 'error']) {
    final val = payload[key];
    if (val is String && val.trim().isNotEmpty) return val.trim();
  }
  return 'Navigation blocked by domain rule.';
}

/// Sends `ui.navigate-from` (`Force: false`) via `/act` to [fromNeuron].
/// Fails open (`null`) when the screen does not handle `ui.navigate-from`;
/// returns the refusal message only when `/act` returns a `Rule` rejection.
Future<String?> checkDepartureGuard({
  required Future<Object?> Function(UiAction action) sendAct,
  required String fromNeuron,
  required String toNeuron,
  String? route,
  bool force = false,
}) async {
  try {
    final response = await sendAct(
      buildNavigateFromActEvent(
        fromNeuron: fromNeuron,
        toNeuron: toNeuron,
        route: route,
        force: force,
      ),
    );
    if (response is Map) {
      return extractRuleRejectionMessage(Map<String, dynamic>.from(response));
    }
    return null;
  } on UiRequestException catch (error) {
    try {
      final decoded = jsonDecode(error.body);
      if (decoded is Map) {
        return extractRuleRejectionMessage(Map<String, dynamic>.from(decoded));
      }
    } catch (_) {
      // Non-JSON error body is not a domain Rule rejection; fail open.
    }
    return null;
  } catch (_) {
    return null;
  }
}

/// Dispatches `ui.navigate-to` to [targetNeuron] via `/act`, failing open if
/// the target is a legacy app without `AggregateScreen` navigation handlers.
Future<bool> dispatchNavigateTo({
  required Future<Object?> Function(UiAction action) sendAct,
  required String targetNeuron,
  String? fromNeuron,
  String? argsJson,
  String transition = 'fade-through',
  String? input,
  String? correlationId,
}) async {
  try {
    final response = await sendAct(
      buildNavigateToActEvent(
        targetNeuron: targetNeuron,
        fromNeuron: fromNeuron,
        argsJson: argsJson,
        transition: transition,
        input: input,
        correlationId: correlationId,
      ),
    );
    return response is Map && response['delivered'] == true;
  } catch (_) {
    return false;
  }
}
