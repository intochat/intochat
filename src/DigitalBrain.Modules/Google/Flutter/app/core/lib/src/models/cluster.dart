// Mirrors DigitalBrain.Microsoft.Aspire.SiloState: one silo of the cluster and the build it runs.
final class SiloState {
  const SiloState({
    required this.id,
    required this.sha,
    required this.state,
    this.health,
  });
  final String id, sha, state;
  final String? health;

  factory SiloState.fromJson(Map<String, dynamic> json) => SiloState(
    id: json['id'] as String,
    sha: json['sha'] as String? ?? '',
    state: json['state'] as String? ?? 'Unknown',
    health: json['health'] as String?,
  );

  @override
  bool operator ==(Object other) =>
      other is SiloState &&
      other.id == id &&
      other.sha == sha &&
      other.state == state &&
      other.health == health;

  @override
  int get hashCode => Object.hash(id, sha, state, health);
}

// Mirrors the ClusterChanged signal: the cluster's stage, the build it stands on, and every silo.
final class ClusterChanged {
  const ClusterChanged({
    required this.stage,
    required this.sha,
    required this.silos,
  });
  final String stage, sha;
  final List<SiloState> silos;

  factory ClusterChanged.fromJson(Map<String, dynamic> json) => ClusterChanged(
    stage: json['stage'] as String? ?? 'steady',
    sha: json['sha'] as String? ?? '',
    silos: [
      for (final silo in json['silos'] as List? ?? const [])
        SiloState.fromJson(Map<String, dynamic>.from(silo as Map)),
    ],
  );
}

// One event of GET /brains/{id}/live: the signal's simple type name, the neuron that published it,
// and the signal itself as camelCase JSON.
final class LiveEnvelope {
  const LiveEnvelope({
    required this.type,
    required this.publisher,
    required this.payload,
  });
  final String type, publisher;
  final Map<String, dynamic> payload;

  factory LiveEnvelope.fromJson(Map<String, dynamic> json) => LiveEnvelope(
    type: json['type'] as String,
    publisher: json['publisher'] as String? ?? '',
    payload: Map<String, dynamic>.from(json['payload'] as Map? ?? const {}),
  );
}
