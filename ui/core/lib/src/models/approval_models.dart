// Dart mirror of an open confirmation (camelCase JSON, PascalCase enum strings).
// Contract: src/Assets/wire/approval.json. requested/expires stay wire strings.

enum ApprovalKind {
  flowStep('FlowStep'),
  synapseStaged('SynapseStaged'),
  storageWrite('StorageWrite'),
  grant('Grant'),
  other('Other');

  const ApprovalKind(this.wire);
  final String wire;

  static ApprovalKind fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown ApprovalKind', value),
  );
}

final class Approval {
  const Approval({
    required this.planId,
    required this.kind,
    required this.subject,
    required this.title,
    this.detail,
    required this.requested,
    this.expires,
    required this.traceId,
  });
  final String planId;
  final ApprovalKind kind;
  final String subject;
  final String title;
  final String? detail;
  final String requested;
  final String? expires;
  final String traceId;

  factory Approval.fromJson(Map<String, dynamic> json) => Approval(
    planId: json['planId'] as String,
    kind: ApprovalKind.fromWire(json['kind'] as String),
    subject: json['subject'] as String,
    title: json['title'] as String,
    detail: json['detail'] as String?,
    requested: json['requested'] as String,
    expires: json['expires'] as String?,
    traceId: json['traceId'] as String,
  );

  Map<String, dynamic> toJson() => {
    'planId': planId,
    'kind': kind.wire,
    'subject': subject,
    'title': title,
    'detail': detail,
    'requested': requested,
    'expires': expires,
    'traceId': traceId,
  };
}
