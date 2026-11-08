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
  });
  final int sequence;
  final String type;
  final String publisher;
  final Map<String, dynamic> payload;
  final String at;
  final String? traceId;
  final String? causationId;

  factory JournaledSignalView.fromJson(Map<String, dynamic> json) =>
      JournaledSignalView(
        sequence: (json['sequence'] as num).toInt(),
        type: json['type'] as String,
        publisher: json['publisher'] as String,
        payload: (json['payload'] as Map).cast<String, dynamic>(),
        at: json['at'] as String,
        traceId: json['traceId'] as String?,
        causationId: json['causationId'] as String?,
      );

  Map<String, dynamic> toJson() => {
    'sequence': sequence,
    'type': type,
    'publisher': publisher,
    'payload': payload,
    'at': at,
    'traceId': traceId,
    'causationId': causationId,
  };
}
