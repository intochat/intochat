class InstalledAppSummary {
  const InstalledAppSummary({
    required this.id,
    required this.title,
    required this.description,
    required this.status,
    required this.version,
    required this.operations,
    this.icon,
    this.publishedVersion,
    this.edited = false,
    this.updateAvailable = false,
  });
  final String id, title, description, status, version;
  final List<String> operations;
  final String? icon, publishedVersion;
  final bool edited, updateAvailable;
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
        icon: json['icon'] as String?,
        publishedVersion: json['publishedVersion'] as String?,
        edited: json['edited'] == true,
        updateAvailable: json['updateAvailable'] == true,
      );
}
