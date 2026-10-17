/// Money is micro-credits: 1 credit = 1 USD / 1_000_000. Display as dollars.
class ComputeSummary {
  const ComputeSummary({
    required this.balanceMicros,
    required this.spentMicros,
    this.rows = const [],
  });
  final int balanceMicros, spentMicros;
  final List<ComputeRow> rows;
  factory ComputeSummary.fromJson(Map<String, dynamic> json) => ComputeSummary(
    balanceMicros: (json['balanceMicros'] as num?)?.toInt() ?? 0,
    spentMicros: (json['spentMicros'] as num?)?.toInt() ?? 0,
    rows: [
      for (final row in json['rows'] as List? ?? const [])
        ComputeRow.fromJson(Map<String, dynamic>.from(row as Map)),
    ],
  );
}

class ComputeRow {
  const ComputeRow({required this.key, required this.micros, this.count = 0});
  final String key;
  final int micros, count;
  factory ComputeRow.fromJson(Map<String, dynamic> json) => ComputeRow(
    key: json['key'] as String,
    micros: (json['micros'] as num?)?.toInt() ?? 0,
    count: (json['count'] as num?)?.toInt() ?? 0,
  );
}

class ComputeCost {
  const ComputeCost({
    required this.traceId,
    required this.costId,
    required this.kind,
    required this.item,
    this.quantity = 0,
    required this.unit,
    required this.micros,
    this.synapse,
    this.app,
    this.neuron,
    this.at,
  });
  final String traceId, costId, kind, item, unit;
  final double quantity;
  final int micros;
  final String? synapse, app, neuron;
  final DateTime? at;
  factory ComputeCost.fromJson(Map<String, dynamic> json) => ComputeCost(
    traceId: json['traceId'] as String,
    costId: json['costId'] as String,
    kind: json['kind'] as String,
    item: json['item'] as String,
    quantity: (json['quantity'] as num?)?.toDouble() ?? 0,
    unit: json['unit'] as String,
    micros: (json['micros'] as num?)?.toInt() ?? 0,
    synapse: json['synapse'] as String?,
    app: json['app'] as String?,
    neuron: json['neuron'] as String?,
    at: DateTime.tryParse(json['at'] as String? ?? ''),
  );
}
