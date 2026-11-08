// Dart mirrors of the Ino presence and focus wire records (camelCase JSON,
// PascalCase enum strings). Contract: src/Assets/wire/presence.json, focus.json.

enum PresenceState {
  idle('Idle'),
  listening('Listening'),
  working('Working'),
  searching('Searching'),
  waiting('Waiting'),
  attention('Attention'),
  paused('Paused'),
  disconnected('Disconnected');

  const PresenceState(this.wire);
  final String wire;

  static PresenceState fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown PresenceState', value),
  );
}

enum FocusTone {
  info('Info'),
  success('Success'),
  warning('Warning'),
  critical('Critical');

  const FocusTone(this.wire);
  final String wire;

  static FocusTone fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown FocusTone', value),
  );
}

final class FocusAction {
  const FocusAction({required this.kind, required this.argument});
  final String kind;
  final String argument;

  factory FocusAction.fromJson(Map<String, dynamic> json) => FocusAction(
    kind: json['kind'] as String,
    argument: json['argument'] as String,
  );

  Map<String, dynamic> toJson() => {'kind': kind, 'argument': argument};
}

final class Focus {
  const Focus({
    required this.target,
    required this.tone,
    required this.reason,
    this.action,
  });
  final String target;
  final FocusTone tone;
  final String reason;
  final FocusAction? action;

  factory Focus.fromJson(Map<String, dynamic> json) => Focus(
    target: json['target'] as String,
    tone: FocusTone.fromWire(json['tone'] as String),
    reason: json['reason'] as String,
    action: json['action'] == null
        ? null
        : FocusAction.fromJson((json['action'] as Map).cast<String, dynamic>()),
  );

  Map<String, dynamic> toJson() => {
    'target': target,
    'tone': tone.wire,
    'reason': reason,
    'action': action?.toJson(),
  };
}

final class Presence {
  const Presence({
    required this.state,
    this.flowId,
    this.step,
    this.flowType,
    this.neuronKind,
    this.traceId,
    this.focus,
    this.reason,
    required this.revision,
  });
  final PresenceState state;
  final String? flowId;
  final String? step;
  final String? flowType;
  final String? neuronKind;
  final String? traceId;
  final Focus? focus;
  final String? reason;
  final int revision;

  factory Presence.fromJson(Map<String, dynamic> json) => Presence(
    state: PresenceState.fromWire(json['state'] as String),
    flowId: json['flowId'] as String?,
    step: json['step'] as String?,
    flowType: json['flowType'] as String?,
    neuronKind: json['neuronKind'] as String?,
    traceId: json['traceId'] as String?,
    focus: json['focus'] == null
        ? null
        : Focus.fromJson((json['focus'] as Map).cast<String, dynamic>()),
    reason: json['reason'] as String?,
    revision: (json['revision'] as num).toInt(),
  );

  Map<String, dynamic> toJson() => {
    'state': state.wire,
    'flowId': flowId,
    'step': step,
    'flowType': flowType,
    'neuronKind': neuronKind,
    'traceId': traceId,
    'focus': focus?.toJson(),
    'reason': reason,
    'revision': revision,
  };
}
