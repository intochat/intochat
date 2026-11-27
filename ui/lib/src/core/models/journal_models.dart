// Dart mirror of a journaled signal. Contract: src/Assets/wire/journaled-signal.json.
// payload is opaque (its keys keep the publisher casing); at stays the wire string.

final class JournaledSignalView {
  const JournaledSignalView({
    required this.sequence,
    required this.type,
    required this.publisher,
    required this.payload,
    required this.at,
    this.traceId,
    this.causationId,
    this.alias,
  });
  final int sequence;
  final String type;
  final String publisher;
  final Map<String, dynamic> payload;
  final String at;
  final String? traceId;
  final String? causationId;

  /// The journal alias of the signal ([type] is the payload's record name); null when the source does not say.
  final String? alias;

  factory JournaledSignalView.fromJson(Map<String, dynamic> json) =>
      JournaledSignalView(
        sequence: (json['sequence'] as num).toInt(),
        type: json['type'] as String,
        publisher: json['publisher'] as String,
        payload: (json['payload'] as Map).cast<String, dynamic>(),
        at: json['at'] as String,
        traceId: json['traceId'] as String?,
        causationId: json['causationId'] as String?,
        alias: json['alias'] as String?,
      );

  Map<String, dynamic> toJson() => {
    'sequence': sequence,
    'type': type,
    'publisher': publisher,
    'payload': payload,
    'at': at,
    'traceId': traceId,
    'causationId': causationId,
    if (alias != null) 'alias': alias,
  };
}

/// One signal of the journal tail (`GET /brains/{id}/journal/tail`): who published it under which alias, never the payload.
final class JournalTailSignal {
  const JournalTailSignal({
    required this.seq,
    required this.publisher,
    required this.alias,
    required this.at,
    this.traceId,
    this.causationId,
  });
  final int seq;
  final String publisher, alias;
  final DateTime at;
  final String? traceId, causationId;

  factory JournalTailSignal.fromJson(Map<String, dynamic> json) =>
      JournalTailSignal(
        seq: (json['seq'] as num).toInt(),
        publisher: json['publisher'] as String,
        alias: json['alias'] as String,
        at: DateTime.parse(json['at'] as String),
        traceId: json['traceId'] as String?,
        causationId: json['causationId'] as String?,
      );

  /// The signals of one `journaled-signal` event's data, `{"signals":[...]}`.
  static List<JournalTailSignal> batch(Map<String, dynamic> json) =>
      (json['signals'] as List? ?? [])
          .map((item) => JournalTailSignal.fromJson((item as Map).cast()))
          .toList(growable: false);
}
