// Dart mirror of the flow index view (camelCase JSON, PascalCase enum strings).
// Contract: src/Assets/wire/flow-view.json. Timestamps (started, ended) and a
// relation's lag stay the wire strings: DateTime would not reproduce the
// "+00:00" offset form, and the sample strings are the contract.

enum FlowStatus {
  pending('Pending'),
  running('Running'),
  waiting('Waiting'),
  blocked('Blocked'),
  completed('Completed'),
  failed('Failed'),
  cancelled('Cancelled');

  const FlowStatus(this.wire);
  final String wire;

  static FlowStatus fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown FlowStatus', value),
  );
}

enum WaitReason {
  approval('Approval'),
  external('External'),
  schedule('Schedule');

  const WaitReason(this.wire);
  final String wire;

  static WaitReason fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown WaitReason', value),
  );
}

enum RelationKind {
  blockedBy('BlockedBy'),
  blocks('Blocks'),
  follows('Follows'),
  precedes('Precedes'),
  subtask('Subtask'),
  parent('Parent'),
  relates('Relates'),
  duplicates('Duplicates');

  const RelationKind(this.wire);
  final String wire;

  static RelationKind fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown RelationKind', value),
  );
}

final class FlowRelation {
  const FlowRelation({
    required this.targetFlowId,
    required this.kind,
    this.lag,
  });
  final String targetFlowId;
  final RelationKind kind;
  final String? lag;

  factory FlowRelation.fromJson(Map<String, dynamic> json) => FlowRelation(
    targetFlowId: json['targetFlowId'] as String,
    kind: RelationKind.fromWire(json['kind'] as String),
    lag: json['lag'] as String?,
  );

  Map<String, dynamic> toJson() => {
    'targetFlowId': targetFlowId,
    'kind': kind.wire,
    'lag': lag,
  };
}

List<String> _strings(Object? value) =>
    (value as List? ?? const []).map((e) => '$e').toList(growable: false);

final class FlowView {
  const FlowView({
    required this.flowId,
    required this.type,
    required this.title,
    required this.owner,
    required this.traceId,
    required this.status,
    required this.steps,
    required this.done,
    this.current,
    this.waitingFor,
    this.waitingStep,
    this.confirmationPlanId,
    required this.fraction,
    required this.relations,
    required this.blockedBy,
    this.parentFlowId,
    required this.started,
    this.ended,
    this.detail,
    this.app,
    this.conversation,
  });
  final String flowId;
  final String type;
  final String title;
  final String owner;
  final String traceId;
  final FlowStatus status;
  final List<String> steps;
  final List<String> done;
  final String? current;
  final WaitReason? waitingFor;
  final String? waitingStep;
  final String? confirmationPlanId;
  final double fraction;
  final List<FlowRelation> relations;
  final List<String> blockedBy;
  final String? parentFlowId;
  final String started;
  final String? ended;
  final String? detail;
  final String? app;
  final String? conversation;

  factory FlowView.fromJson(Map<String, dynamic> json) => FlowView(
    flowId: json['flowId'] as String,
    type: json['type'] as String,
    title: json['title'] as String,
    owner: json['owner'] as String,
    traceId: json['traceId'] as String,
    status: FlowStatus.fromWire(json['status'] as String),
    steps: _strings(json['steps']),
    done: _strings(json['done']),
    current: json['current'] as String?,
    waitingFor: json['waitingFor'] == null
        ? null
        : WaitReason.fromWire(json['waitingFor'] as String),
    waitingStep: json['waitingStep'] as String?,
    confirmationPlanId: json['confirmationPlanId'] as String?,
    fraction: (json['fraction'] as num).toDouble(),
    relations: (json['relations'] as List? ?? const [])
        .whereType<Map>()
        .map((e) => FlowRelation.fromJson(e.cast<String, dynamic>()))
        .toList(growable: false),
    blockedBy: _strings(json['blockedBy']),
    parentFlowId: json['parentFlowId'] as String?,
    started: json['started'] as String,
    ended: json['ended'] as String?,
    detail: json['detail'] as String?,
    app: json['app'] as String?,
    conversation: json['conversation'] as String?,
  );

  Map<String, dynamic> toJson() => {
    'flowId': flowId,
    'type': type,
    'title': title,
    'owner': owner,
    'traceId': traceId,
    'status': status.wire,
    'steps': steps,
    'done': done,
    'current': current,
    'waitingFor': waitingFor?.wire,
    'waitingStep': waitingStep,
    'confirmationPlanId': confirmationPlanId,
    'fraction': fraction,
    'relations': relations.map((e) => e.toJson()).toList(),
    'blockedBy': blockedBy,
    'parentFlowId': parentFlowId,
    'started': started,
    'ended': ended,
    'detail': detail,
    'app': app,
    'conversation': conversation,
  };
}
