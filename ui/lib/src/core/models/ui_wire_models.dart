import '../../generated/node_models.dart';
import 'workspace_models.dart';

// ── Client -> Kernel UI Actions ──────────────────────────────────────────────

/// Typed wire envelope for a person's action or host report dispatched through
/// [IntoChatOSUiClient.uiEvent].
///
/// Mirrors C# `AppUiEndpoints.ActRequest`, `ConfirmationEndpoints`, and
/// `WebBrowserEndpoints` 1:1.
sealed class UiAction {
  const UiAction();

  /// Wire discriminator (`'act'`, `'confirmation'`, or `'webbrowser'`).
  String get kind;

  /// Full wire representation including [kind] (for round-trip serialization).
  Map<String, Object?> toJson();

  /// HTTP request body for the target endpoint (without client-only routing keys).
  Map<String, Object?> toRequestJson();

  /// Decodes a [UiAction] from its JSON wire representation.
  /// Throws [ArgumentError] for unknown kinds or browser actions.
  static UiAction fromJson(Map<String, dynamic> json) => switch (json['kind']) {
    'act' => ActAction.fromJson(json),
    'confirmation' => ConfirmationAction.fromJson(json),
    'webbrowser' => WebBrowserAction.fromJson(json),
    final other => throw ArgumentError.value(other, 'kind', 'Unknown UI event'),
  };
}

/// Command sent to `POST /brains/{workspace}/act`.
///
/// Mirrors C# `AppUiEndpoints.ActRequest(Neuron, Target, Signal, Args, Value)`.
final class ActAction extends UiAction {
  const ActAction({
    required this.neuron,
    required this.target,
    required this.signal,
    this.args,
    this.value,
  });

  final String neuron;
  final String target;
  final String signal;
  final Object? args;
  final String? value;

  @override
  String get kind => 'act';

  factory ActAction.fromJson(Map<String, dynamic> json) {
    final neuron = json['neuron'];
    final target = json['target'];
    final signal = json['signal'];
    if (neuron is! String || target is! String || signal is! String) {
      throw ArgumentError(
        'ActAction requires string neuron, target, and signal.',
      );
    }
    return ActAction(
      neuron: neuron,
      target: target,
      signal: signal,
      args: json['args'],
      value: json['value'] as String?,
    );
  }

  @override
  Map<String, Object?> toRequestJson() => {
    'neuron': neuron,
    'target': target,
    'signal': signal,
    'args': args,
    'value': ?value,
  };

  @override
  Map<String, Object?> toJson() => {'kind': kind, ...toRequestJson()};

  @override
  String toString() =>
      'ActAction(neuron: $neuron, target: $target, signal: $signal, args: $args, value: $value)';
}

/// Confirmation decision sent to `POST /brains/{workspace}/confirmations/{planId}`.
final class ConfirmationAction extends UiAction {
  const ConfirmationAction({required this.planId, required this.approve});

  final String planId;
  final bool approve;

  @override
  String get kind => 'confirmation';

  factory ConfirmationAction.fromJson(Map<String, dynamic> json) {
    final planId = json['planId'];
    final approve = json['approve'];
    if (planId is! String || approve is! bool) {
      throw ArgumentError(
        'ConfirmationAction requires string planId and bool approve.',
      );
    }
    return ConfirmationAction(planId: planId, approve: approve);
  }

  @override
  Map<String, Object?> toRequestJson() => {'approve': approve};

  @override
  Map<String, Object?> toJson() => {
    'kind': kind,
    'planId': planId,
    'approve': approve,
  };

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is ConfirmationAction &&
          other.planId == planId &&
          other.approve == approve;

  @override
  int get hashCode => Object.hash(planId, approve);

  @override
  String toString() => 'ConfirmationAction(planId: $planId, approve: $approve)';
}

/// Embedded browser lifecycle report sent to
/// `POST /brains/{workspace}/browsers/{connect|disconnect|unavailable}`.
///
/// Mirrors C# `WebBrowserEndpoints.BrowserConnect`, `BrowserDisconnect`, and
/// `BrowserUnavailable`.
sealed class WebBrowserAction extends UiAction {
  const WebBrowserAction({required this.name});

  final String name;

  /// Route segment under `/browsers/`: `'connect'`, `'disconnect'`, or `'unavailable'`.
  String get action;

  @override
  String get kind => 'webbrowser';

  static WebBrowserAction fromJson(Map<String, dynamic> json) {
    final name = json['name'];
    if (name is! String) {
      throw ArgumentError.value(name, 'name', 'Browser action requires name.');
    }
    return switch (json['action']) {
      'connect' => WebBrowserConnectAction(
        name: name,
        port: (json['port'] as num).toInt(),
        sessionId: json['sessionId'] as String,
      ),
      'disconnect' => WebBrowserDisconnectAction(
        name: name,
        sessionId: json['sessionId'] as String,
      ),
      'unavailable' => WebBrowserUnavailableAction(
        name: name,
        reason: json['reason'] as String,
      ),
      final other => throw ArgumentError.value(
        other,
        'action',
        'Unknown browser action.',
      ),
    };
  }
}

/// Mirrors C# `WebBrowserEndpoints.BrowserConnect(Name, Port, SessionId)`.
final class WebBrowserConnectAction extends WebBrowserAction {
  const WebBrowserConnectAction({
    required super.name,
    required this.port,
    required this.sessionId,
  });

  final int port;
  final String sessionId;

  @override
  String get action => 'connect';

  @override
  Map<String, Object?> toRequestJson() => {
    'name': name,
    'port': port,
    'sessionId': sessionId,
  };

  @override
  Map<String, Object?> toJson() => {
    'kind': kind,
    'action': action,
    ...toRequestJson(),
  };

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is WebBrowserConnectAction &&
          other.name == name &&
          other.port == port &&
          other.sessionId == sessionId;

  @override
  int get hashCode => Object.hash(name, port, sessionId);
}

/// Mirrors C# `WebBrowserEndpoints.BrowserDisconnect(Name, SessionId)`.
final class WebBrowserDisconnectAction extends WebBrowserAction {
  const WebBrowserDisconnectAction({
    required super.name,
    required this.sessionId,
  });

  final String sessionId;

  @override
  String get action => 'disconnect';

  @override
  Map<String, Object?> toRequestJson() => {
    'name': name,
    'sessionId': sessionId,
  };

  @override
  Map<String, Object?> toJson() => {
    'kind': kind,
    'action': action,
    ...toRequestJson(),
  };

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is WebBrowserDisconnectAction &&
          other.name == name &&
          other.sessionId == sessionId;

  @override
  int get hashCode => Object.hash(name, sessionId);
}

/// Mirrors C# `WebBrowserEndpoints.BrowserUnavailable(Name, Reason)`.
final class WebBrowserUnavailableAction extends WebBrowserAction {
  const WebBrowserUnavailableAction({
    required super.name,
    required this.reason,
  });

  final String reason;

  @override
  String get action => 'unavailable';

  @override
  Map<String, Object?> toRequestJson() => {'name': name, 'reason': reason};

  @override
  Map<String, Object?> toJson() => {
    'kind': kind,
    'action': action,
    ...toRequestJson(),
  };

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is WebBrowserUnavailableAction &&
          other.name == name &&
          other.reason == reason;

  @override
  int get hashCode => Object.hash(name, reason);
}

// ── Kernel -> Client Node Read Payloads ──────────────────────────────────────

/// Dart mirror of C# `IntoChat.OS.Modules.Flutter.ViewRef(Kind, Name)`.
final class UiViewRef {
  const UiViewRef({required this.kind, required this.name});

  final String kind;
  final String name;

  factory UiViewRef.fromJson(Map<String, dynamic> json) => UiViewRef(
    kind: (json['kind'] as String?) ?? '',
    name: (json['name'] as String?) ?? '',
  );

  Map<String, Object?> toJson() => {'kind': kind, 'name': name};

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is UiViewRef && other.kind == kind && other.name == name;

  @override
  int get hashCode => Object.hash(kind, name);

  @override
  String toString() => 'UiViewRef(kind: $kind, name: $name)';
}

/// Typed wire envelope returned by `IntoChatOSUiClient.readNode` (`GET /apps/node`
/// and `GET /apps/{name}/children/{path}`), aligned 1:1 with C# `ShellUiJsonContext`.
sealed class UiNodePayload {
  const UiNodePayload();

  /// Decodes a JSON object returned for [kind] into its typed [UiNodePayload].
  static UiNodePayload fromJson(String kind, Map<String, dynamic> json) =>
      switch (kind) {
        'view' ||
        'child' ||
        'confirmation' ||
        'screen' => PublishedViewPayload.fromJson(json),
        'viewport' => ViewportPayload.fromJson(json),
        'webbrowser' => WebBrowserPayload.fromJson(json),
        'connect' => ConnectPayload.fromJson(json),
        'error' => ErrorPayload.fromJson(json),
        _ => ErrorPayload(error: 'Unknown node kind: $kind'),
      };

  /// Decodes an arbitrary JSON value (including `null` when a screen or view is
  /// not yet loaded) for [kind].
  static UiNodePayload fromDynamic(String kind, Object? raw) {
    if (raw == null) {
      return switch (kind) {
        'view' ||
        'child' ||
        'confirmation' ||
        'screen' => const PublishedViewPayload(),
        'viewport' => const ViewportPayload(),
        'webbrowser' => const WebBrowserPayload(),
        'connect' => const ConnectPayload(),
        _ => ErrorPayload(error: 'Empty payload for kind: $kind'),
      };
    }
    if (raw is Map) {
      return fromJson(kind, Map<String, dynamic>.from(raw));
    }
    throw FormatException(
      'Expected JSON object for node kind "$kind", got ${raw.runtimeType}.',
    );
  }
}

/// Published view payload covering both C# `PublishedView(Alias, Generation, View)`
/// and direct `DigitalBrain.UI.View` (such as the confirmation inbox) as well as
/// an empty/preparing view (`view == null`).
final class PublishedViewPayload extends UiNodePayload {
  const PublishedViewPayload({
    this.alias,
    this.generation = 0,
    this.view,
    this.acts,
  });

  final String? alias;
  final int generation;
  final AppView? view;

  /// Optional wire `acts` discriminator on the inner view object (e.g. `'act'`
  /// for `IScreen` views).
  final String? acts;

  bool get isScreenView => acts == 'act';
  bool get hasView => view != null;

  factory PublishedViewPayload.fromJson(Map<String, dynamic> json) {
    final rawView = json['view'] is Map
        ? Map<String, dynamic>.from(json['view'] as Map)
        : json;
    final view = rawView['root'] is Map ? AppView.fromJson(rawView) : null;
    return PublishedViewPayload(
      alias: json['alias'] is String ? json['alias'] as String : null,
      generation: json['generation'] is num
          ? (json['generation'] as num).toInt()
          : 0,
      view: view,
      acts: rawView['acts'] is String ? rawView['acts'] as String : null,
    );
  }
}

/// Viewport payload covering C# `UnitViewportResponse`, `EmptyViewportResponse`,
/// and `TargetedViewportResponse` in `ShellUiJsonContext`.
final class ViewportPayload extends UiNodePayload {
  const ViewportPayload({
    this.generation,
    this.revision,
    this.target,
    this.root,
    this.error,
  });

  /// Present on `UnitViewportResponse`.
  final int? generation;

  /// Present on `EmptyViewportResponse` and `TargetedViewportResponse`.
  final int? revision;

  /// Present on `TargetedViewportResponse` (and nullable on `UnitViewportResponse`).
  final WorkspaceViewReference? target;

  /// Target view or child surface to host inside the viewport.
  final UiViewRef? root;

  /// Optional error message shown above [root] or centered when [root] is null.
  final String? error;

  factory ViewportPayload.fromJson(Map<String, dynamic> json) {
    final rawRoot = json['root'];
    final root =
        rawRoot is Map && rawRoot['kind'] is String && rawRoot['name'] is String
        ? UiViewRef.fromJson(Map<String, dynamic>.from(rawRoot))
        : null;
    final rawTarget = json['target'];
    final target = rawTarget is Map
        ? WorkspaceViewReference.fromJson(Map<String, dynamic>.from(rawTarget))
        : null;
    return ViewportPayload(
      generation: json['generation'] is num
          ? (json['generation'] as num).toInt()
          : null,
      revision: json['revision'] is num
          ? (json['revision'] as num).toInt()
          : null,
      target: target,
      root: root,
      error: json['error'] is String ? json['error'] as String : null,
    );
  }

  Map<String, Object?> toJson() => {
    'generation': ?generation,
    'revision': ?revision,
    'target': ?target?.toJson(),
    'root': ?root?.toJson(),
    'error': ?error,
  };
}

/// Web browser node payload mirroring C# `WebBrowserView` in `ShellUiJsonContext`.
final class WebBrowserPayload extends UiNodePayload {
  const WebBrowserPayload({
    this.name = '',
    this.version = 0,
    this.uri = '',
    this.title = '',
    this.connection = '',
    this.error = '',
    this.actualUrl = '',
  });

  final String name;
  final int version;
  final String uri;
  final String title;
  final String connection;
  final String error;
  final String actualUrl;

  factory WebBrowserPayload.fromJson(Map<String, dynamic> json) {
    final definition = json['definition'] is Map
        ? Map<String, dynamic>.from(json['definition'] as Map)
        : json;
    return WebBrowserPayload(
      name: (definition['name'] as String?) ?? (json['name'] as String?) ?? '',
      version: json['version'] is num ? (json['version'] as num).toInt() : 0,
      uri: (definition['uri'] as String?) ?? '',
      title:
          (definition['title'] as String?) ?? (json['title'] as String?) ?? '',
      connection: json['connection'] is String
          ? json['connection'] as String
          : '',
      error: json['error'] is String ? json['error'] as String : '',
      actualUrl: json['actualUrl'] is String ? json['actualUrl'] as String : '',
    );
  }

  Map<String, Object?> toJson() => {
    'name': name,
    'version': version,
    'uri': uri,
    'title': title,
    'connection': connection,
    'error': error,
    'actualUrl': actualUrl,
  };
}

/// Connect response payload mirroring C# `ConnectResponse(Root, ActivationApp)`.
final class ConnectPayload extends UiNodePayload {
  const ConnectPayload({this.root, this.activationApp});

  final UiViewRef? root;
  final String? activationApp;

  factory ConnectPayload.fromJson(Map<String, dynamic> json) {
    final rawRoot = json['root'];
    final root =
        rawRoot is Map && rawRoot['kind'] is String && rawRoot['name'] is String
        ? UiViewRef.fromJson(Map<String, dynamic>.from(rawRoot))
        : null;
    return ConnectPayload(
      root: root,
      activationApp: json['activationApp'] is String
          ? json['activationApp'] as String
          : null,
    );
  }

  Map<String, Object?> toJson() => {
    'root': root?.toJson(),
    'activationApp': activationApp,
  };
}

/// Error payload mirroring C# `UiErrorResponse(Error, Reason)`.
final class ErrorPayload extends UiNodePayload {
  const ErrorPayload({required this.error, this.reason});

  final String error;
  final String? reason;

  factory ErrorPayload.fromJson(Map<String, dynamic> json) => ErrorPayload(
    error: (json['error'] as String?) ?? '',
    reason: json['reason'] is String ? json['reason'] as String : null,
  );

  Map<String, Object?> toJson() => {'error': error, 'reason': ?reason};
}
