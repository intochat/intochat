enum OsNotificationPriority {
  low,
  normal,
  high,
  urgent;

  static OsNotificationPriority parse(Object? raw) {
    if (raw is int) {
      return switch (raw) {
        0 => OsNotificationPriority.low,
        2 => OsNotificationPriority.high,
        3 => OsNotificationPriority.urgent,
        _ => OsNotificationPriority.normal,
      };
    }
    if (raw is String) {
      return switch (raw.toLowerCase()) {
        'low' => OsNotificationPriority.low,
        'high' => OsNotificationPriority.high,
        'urgent' => OsNotificationPriority.urgent,
        _ => OsNotificationPriority.normal,
      };
    }
    return OsNotificationPriority.normal;
  }
}

final class OsNotificationItem {
  const OsNotificationItem({
    required this.id,
    required this.brainId,
    required this.sourceApp,
    required this.title,
    required this.body,
    required this.priority,
    this.category,
    this.actionUrl,
    required this.read,
    required this.createdUtc,
    this.readUtc,
  });

  final String id;
  final String brainId;
  final String sourceApp;
  final String title;
  final String body;
  final OsNotificationPriority priority;
  final String? category;
  final String? actionUrl;
  final bool read;
  final DateTime createdUtc;
  final DateTime? readUtc;

  factory OsNotificationItem.fromJson(Map<String, dynamic> json) {
    return OsNotificationItem(
      id: (json['id'] as String?) ?? '',
      brainId: (json['brainId'] as String?) ?? '',
      sourceApp: (json['sourceApp'] as String?) ?? 'os',
      title: (json['title'] as String?) ?? '',
      body: (json['body'] as String?) ?? '',
      priority: OsNotificationPriority.parse(json['priority']),
      category: json['category'] as String?,
      actionUrl: json['actionUrl'] as String?,
      read: json['read'] == true,
      createdUtc:
          DateTime.tryParse((json['createdUtc'] as String?) ?? '') ??
          DateTime.now().toUtc(),
      readUtc: DateTime.tryParse((json['readUtc'] as String?) ?? ''),
    );
  }

  OsNotificationItem copyWith({bool? read, DateTime? readUtc}) {
    return OsNotificationItem(
      id: id,
      brainId: brainId,
      sourceApp: sourceApp,
      title: title,
      body: body,
      priority: priority,
      category: category,
      actionUrl: actionUrl,
      read: read ?? this.read,
      createdUtc: createdUtc,
      readUtc: readUtc ?? this.readUtc,
    );
  }
}

final class OsNotificationList {
  const OsNotificationList({required this.items, required this.unreadCount});

  final List<OsNotificationItem> items;
  final int unreadCount;

  factory OsNotificationList.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? [
            for (final item in rawItems)
              if (item is Map)
                OsNotificationItem.fromJson(Map<String, dynamic>.from(item)),
          ]
        : const <OsNotificationItem>[];
    final unreadRaw = json['unreadCount'];
    final unreadCount = unreadRaw is int
        ? unreadRaw
        : items.where((item) => !item.read).length;
    return OsNotificationList(items: items, unreadCount: unreadCount);
  }
}
