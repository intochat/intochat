import 'package:flutter/foundation.dart';

/// App lifecycle operations dispatched through [AppLifecycleActivation].
enum AppLifecycleKind {
  install('install'),
  update('update'),
  remove('remove');

  const AppLifecycleKind(this.wire);
  final String wire;

  static AppLifecycleKind? tryParse(Object? raw) => switch (raw) {
    'install' => AppLifecycleKind.install,
    'update' => AppLifecycleKind.update,
    'remove' => AppLifecycleKind.remove,
    _ => null,
  };
}

String? _nonEmptyString(Object? value) {
  if (value is! String) return null;
  return value.trim().isEmpty ? null : value;
}

/// Sealed domain hierarchy for all shell activation envelopes routed by
/// `ShellActivationRouter`.
///
/// Legacy wire envelopes such as `{'kind': 'settings', 'tab': ...}` and
/// `{'view': 'configuration', 'application': ...}` are normalized once at the
/// wire ingress boundary in [tryFromWireMap] / [fromWireMap].
@immutable
sealed class ShellActivation {
  const ShellActivation();

  /// Parses a raw wire activation map into a typed [ShellActivation].
  ///
  /// Normalizes legacy `{'kind': 'settings', ...}` into
  /// [WorkspaceAppActivation] (`application: 'settings'`, `operation: 'open'`,
  /// `hasNavigationPayload: false`) and `{'view': 'configuration', ...}` into
  /// [WorkspaceAppActivation] (`application: 'apps'`, `operation: 'open'`,
  /// `input: 'installed:<app>'`) at this single ingress boundary.
  ///
  /// Returns `null` when [raw] does not match any supported activation shape.
  static ShellActivation? tryFromWireMap(Map<String, dynamic> raw) {
    final kind = raw['kind'];
    final brainId = _nonEmptyString(raw['brainId']);

    if (kind == 'account') {
      return const AccountActivation();
    }
    if (kind == 'lobby-ended') {
      return const LobbyEndedActivation();
    }
    if (kind == 'reconnect') {
      return ReconnectActivation(
        brainId: brainId,
        application: _nonEmptyString(raw['application']),
        operation: _nonEmptyString(raw['operation']),
        input: raw['input'] is String ? raw['input'] as String : null,
      );
    }
    if (AppLifecycleKind.tryParse(kind) case final lifecycleKind?) {
      final extra = <String, dynamic>{
        for (final entry in raw.entries)
          if (const {
                'kind',
                'brainId',
                'name',
                'request',
                'consent',
                'confirmed',
                'target',
              }.contains(entry.key) ==
              false)
            entry.key: entry.value,
      };
      return AppLifecycleActivation(
        lifecycleKind: lifecycleKind,
        brainId: brainId,
        name: raw['name'] is String ? raw['name'] as String : null,
        request: raw['request'] is Map
            ? Map<String, dynamic>.from(raw['request'] as Map)
            : null,
        consent: raw['consent'] is String ? raw['consent'] as String : null,
        confirmed: raw['confirmed'] == true,
        target: raw['target'] is String ? raw['target'] as String : null,
        extra: extra,
      );
    }
    if (kind == 'connections') {
      final extra = <String, dynamic>{
        for (final entry in raw.entries)
          if (entry.key != 'kind' &&
              entry.key != 'brainId' &&
              entry.key != 'connector' &&
              entry.key != 'connection')
            entry.key: entry.value,
      };
      return ConnectionsActivation(
        brainId: brainId,
        connectorId: raw['connector'] is String
            ? raw['connector'] as String
            : null,
        connectionId: raw['connection'] is String
            ? raw['connection'] as String
            : null,
        extra: extra,
      );
    }
    if (kind == 'os_uri') {
      final uri = '${raw['uri'] ?? ''}';
      return OpenWindowActivation(
        uri: uri,
        intent: raw['intent'] is String ? raw['intent'] as String : null,
        brainId: brainId,
        host: raw['host'] is String ? raw['host'] as String : 'window',
      );
    }
    // Single-boundary normalization for legacy `{kind: 'settings', tab?}` wire envelopes.
    if (kind == 'settings') {
      final tab = raw['tab'] is String ? raw['tab'] as String : '';
      return WorkspaceAppActivation.settings(
        brainId: brainId,
        tab: tab,
      );
    }
    // Single-boundary normalization for `{view: 'configuration', application}`.
    if (raw['view'] == 'configuration' && raw['application'] is String) {
      return WorkspaceAppActivation.configuration(
        brainId: brainId,
        application: raw['application'] as String,
      );
    }

    final application = _nonEmptyString(raw['application']);
    final neuron = _nonEmptyString(raw['neuron']);
    final operation = _nonEmptyString(raw['operation']);
    final rawInput = raw['input'] is String ? raw['input'] as String : null;
    final nonEmptyInput = _nonEmptyString(rawInput);
    final argsJson = _nonEmptyString(raw['argsJson'] ?? raw['args']);
    final rawTransition = _nonEmptyString(raw['transition']);
    final fromNeuron = _nonEmptyString(raw['fromNeuron']);
    final hasNavPayload =
        raw['hasNavigationPayload'] == true ||
        nonEmptyInput != null ||
        argsJson != null ||
        rawTransition != null;

    if (application != null) {
      final extra = <String, dynamic>{
        for (final entry in raw.entries)
          if (!const {
            'kind',
            'brainId',
            'application',
            'neuron',
            'operation',
            'input',
            'argsJson',
            'args',
            'transition',
            'fromNeuron',
            'hasNavigationPayload',
          }.contains(entry.key))
            entry.key: entry.value,
      };
      return WorkspaceAppActivation(
        application: application,
        brainId: brainId,
        neuron: neuron,
        operation: operation,
        input: rawInput,
        argsJson: argsJson,
        rawTransition: rawTransition,
        fromNeuron: fromNeuron,
        hasNavigationPayload: hasNavPayload,
        extra: extra,
        includeWireKind: kind == 'workspace',
      );
    }

    if (neuron != null) {
      return ShowNeuronActivation(
        neuron: neuron,
        brainId: brainId,
        operation: operation,
        input: rawInput,
        argsJson: argsJson,
        rawTransition: rawTransition,
        fromNeuron: fromNeuron,
        hasNavigationPayload: hasNavPayload,
      );
    }

    return null;
  }

  /// Parses [raw] or throws [FormatException] when unrecognized.
  static ShellActivation fromWireMap(Map<String, dynamic> raw) {
    final parsed = tryFromWireMap(raw);
    if (parsed == null) {
      throw FormatException('Unsupported shell activation wire map: $raw');
    }
    return parsed;
  }

  /// Serializes this activation to its canonical wire map.
  Map<String, dynamic> toWireMap({String? brainId});
}

/// Switches or signs into another account.
@immutable
final class AccountActivation extends ShellActivation {
  const AccountActivation();

  @override
  Map<String, dynamic> toWireMap({String? brainId}) => const {
    'kind': 'account',
  };

  @override
  bool operator ==(Object other) => other is AccountActivation;

  @override
  int get hashCode => runtimeType.hashCode;
}

/// Ends the active lobby session.
@immutable
final class LobbyEndedActivation extends ShellActivation {
  const LobbyEndedActivation();

  @override
  Map<String, dynamic> toWireMap({String? brainId}) => const {
    'kind': 'lobby-ended',
  };

  @override
  bool operator ==(Object other) => other is LobbyEndedActivation;

  @override
  int get hashCode => runtimeType.hashCode;
}

/// Reconnects the desktop root (e.g. after switching layouts).
@immutable
final class ReconnectActivation extends ShellActivation {
  const ReconnectActivation({
    this.brainId,
    this.application,
    this.operation,
    this.input,
  });

  final String? brainId;
  final String? application;
  final String? operation;
  final String? input;

  @override
  Map<String, dynamic> toWireMap({String? brainId}) => {
    'kind': 'reconnect',
    if ((brainId ?? this.brainId) case final String b) 'brainId': b,
    if (application != null) 'application': application,
    if (operation != null) 'operation': operation,
    if (input != null) 'input': input,
  };
}

/// Runs an app install, update, or remove lifecycle action.
@immutable
final class AppLifecycleActivation extends ShellActivation {
  const AppLifecycleActivation({
    required this.lifecycleKind,
    this.brainId,
    this.name,
    this.request,
    this.consent,
    this.confirmed = false,
    this.target,
    this.extra = const {},
  });

  final AppLifecycleKind lifecycleKind;
  final String? brainId;
  final String? name;
  final Map<String, dynamic>? request;
  final String? consent;
  final bool confirmed;
  final String? target;
  final Map<String, dynamic> extra;

  @override
  Map<String, dynamic> toWireMap({String? brainId}) => {
    ...extra,
    'kind': lifecycleKind.wire,
    if ((brainId ?? this.brainId) case final String b) 'brainId': b,
    if (name != null) 'name': name,
    if (request != null) 'request': request,
    if (consent != null) 'consent': consent,
    if (confirmed) 'confirmed': true,
    if (target != null) 'target': target,
  };
}

/// Opens the workspace connections dialog.
@immutable
final class ConnectionsActivation extends ShellActivation {
  const ConnectionsActivation({
    this.brainId,
    this.connectorId,
    this.connectionId,
    this.extra = const {},
  });

  final String? brainId;
  final String? connectorId;
  final String? connectionId;
  final Map<String, dynamic> extra;

  @override
  Map<String, dynamic> toWireMap({String? brainId}) => {
    ...extra,
    'kind': 'connections',
    if ((brainId ?? this.brainId) case final String b) 'brainId': b,
    if (connectorId != null) 'connector': connectorId,
    if (connectionId != null) 'connection': connectionId,
  };
}

/// Opens an `os://window/open?...` URI from a client-gated intent.
@immutable
final class OpenWindowActivation extends ShellActivation {
  const OpenWindowActivation({
    required this.uri,
    this.intent,
    this.brainId,
    this.host = 'window',
  });

  final String uri;
  final String? intent;
  final String? brainId;
  final String host;

  @override
  Map<String, dynamic> toWireMap({String? brainId}) => {
    'kind': 'os_uri',
    'uri': uri,
    'host': host,
    if (intent != null) 'intent': intent,
    if ((brainId ?? this.brainId) case final String b) 'brainId': b,
  };
}

/// Focuses or navigates directly to a neuron address without an explicit app ID.
@immutable
final class ShowNeuronActivation extends ShellActivation {
  const ShowNeuronActivation({
    required this.neuron,
    this.brainId,
    this.operation,
    this.input,
    this.argsJson,
    this.rawTransition,
    this.fromNeuron,
    this.hasNavigationPayload = false,
  });

  final String neuron;
  final String? brainId;
  final String? operation;
  final String? input;
  final String? argsJson;
  final String? rawTransition;
  final String? fromNeuron;
  final bool hasNavigationPayload;

  @override
  Map<String, dynamic> toWireMap({String? brainId}) => {
    if ((brainId ?? this.brainId) case final String b) 'brainId': b,
    'neuron': neuron,
    if (operation != null) 'operation': operation,
    if (input != null) 'input': input,
    if (argsJson != null) 'argsJson': argsJson,
    if (rawTransition != null) 'transition': rawTransition,
    if (fromNeuron != null) 'fromNeuron': fromNeuron,
  };
}

/// Opens or invokes an application inside a workspace brain.
@immutable
final class WorkspaceAppActivation extends ShellActivation {
  const WorkspaceAppActivation({
    required this.application,
    this.brainId,
    this.neuron,
    this.operation = 'open',
    this.input,
    this.argsJson,
    this.rawTransition,
    this.fromNeuron,
    this.settingsSection,
    this.hasNavigationPayload = false,
    this.extra = const {},
    this.includeWireKind = true,
  });

  /// Canonical activation for opening the Settings app (optionally on [tab]
  /// or [section]) without dispatching `ui.navigate-to`.
  factory WorkspaceAppActivation.settings({
    String? brainId,
    String tab = '',
    String? section,
  }) => WorkspaceAppActivation(
    application: 'settings',
    brainId: brainId,
    operation: 'open',
    input: tab,
    settingsSection: section,
    hasNavigationPayload: false,
  );

  /// Canonical activation for opening an installed app's configuration view in
  /// the Apps app.
  factory WorkspaceAppActivation.configuration({
    String? brainId,
    required String application,
  }) => WorkspaceAppActivation(
    application: 'apps',
    brainId: brainId,
    operation: 'open',
    input: 'installed:$application',
    hasNavigationPayload: true,
  );

  final String application;
  final String? brainId;
  final String? neuron;
  final String? operation;
  final String? input;
  final String? argsJson;
  final String? rawTransition;
  final String? fromNeuron;
  final String? settingsSection;

  /// Whether this activation carries arrival state that should dispatch
  /// `ui.navigate-to` (`INavigatedTo<TArgs>`) on the target screen.
  final bool hasNavigationPayload;

  final Map<String, dynamic> extra;
  final bool includeWireKind;

  String? get nonEmptyInput => _nonEmptyString(input);

  @override
  Map<String, dynamic> toWireMap({String? brainId}) => {
    ...extra,
    if (includeWireKind) 'kind': 'workspace',
    if ((brainId ?? this.brainId) case final String b) 'brainId': b,
    'application': application,
    if (neuron != null) 'neuron': neuron,
    if (operation != null) 'operation': operation,
    if (input != null) 'input': input,
    if (argsJson != null) 'argsJson': argsJson,
    if (rawTransition != null) 'transition': rawTransition,
    if (fromNeuron != null) 'fromNeuron': fromNeuron,
  };
}
