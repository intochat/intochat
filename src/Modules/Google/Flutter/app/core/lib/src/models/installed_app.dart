class InstalledAppSummary {
  const InstalledAppSummary({
    required this.id,
    required this.title,
    required this.description,
    required this.status,
    required this.version,
    required this.operations,
  });
  final String id, title, description, status, version;
  final List<String> operations;
  factory InstalledAppSummary.fromJson(Map<String, dynamic> json) =>
      InstalledAppSummary(
        id: json['name'] as String,
        title: json['title'] as String? ?? json['name'] as String,
        description: json['description'] as String? ?? '',
        status: json['status'] as String? ?? '',
        version: json['version'] as String? ?? '',
        operations: [
          for (final operation in json['operations'] as List? ?? const [])
            operation as String,
        ],
      );
}
