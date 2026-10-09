// Mirrors DigitalBrain.Microsoft.Aspire.InfrastructureRow: a module and the Aspire resource it stands on.
class InfrastructureRow {
  const InfrastructureRow({
    required this.module,
    required this.resource,
    required this.state,
    this.health,
  });
  final String module, resource, state;
  final String? health;
  factory InfrastructureRow.fromJson(Map<String, dynamic> json) =>
      InfrastructureRow(
        module: json['module'] as String,
        resource: json['resource'] as String,
        state: json['state'] as String? ?? 'Unknown',
        health: json['health'] as String?,
      );
}