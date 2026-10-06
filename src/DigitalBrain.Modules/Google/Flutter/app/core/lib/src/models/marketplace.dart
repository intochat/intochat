// Mirrors the Apps module's Marketplace records; the listing is what a publisher released.
class AppOrigin {
  const AppOrigin({
    required this.publisher,
    required this.name,
    required this.version,
  });
  final String publisher, name, version;
  factory AppOrigin.fromJson(Map<String, dynamic> json) => AppOrigin(
    publisher: json['publisher'] as String? ?? '',
    name: json['name'] as String? ?? '',
    version: json['version'] as String? ?? '',
  );
}

class MarketplaceListing {
  const MarketplaceListing({
    required this.publisher,
    required this.publisherAccountId,
    required this.publisherBrainId,
    required this.name,
    required this.title,
    required this.description,
    required this.version,
    required this.icon,
    required this.scenarios,
  });
  final String publisher, publisherAccountId, publisherBrainId;
  final String name, title, description, version;
  final String? icon;
  final List<String> scenarios;
  factory MarketplaceListing.fromJson(Map<String, dynamic> json) =>
      MarketplaceListing(
        publisher: json['publisher'] as String? ?? '',
        publisherAccountId: json['publisherAccountId'] as String? ?? '',
        publisherBrainId: json['publisherBrainId'] as String? ?? '',
        name: json['name'] as String,
        title: json['title'] as String? ?? json['name'] as String,
        description: json['description'] as String? ?? '',
        version: json['version'] as String? ?? '',
        icon: json['icon'] as String?,
        scenarios: List.unmodifiable([
          for (final scenario in json['scenarios'] as List? ?? const [])
            scenario as String,
        ]),
      );
}

class MarketplaceEntry {
  const MarketplaceEntry({
    required this.listing,
    required this.installed,
    required this.updateAvailable,
  });
  final MarketplaceListing listing;
  final bool installed, updateAvailable;
  factory MarketplaceEntry.fromJson(Map<String, dynamic> json) =>
      MarketplaceEntry(
        listing: MarketplaceListing.fromJson(
          Map<String, dynamic>.from(json['listing'] as Map),
        ),
        installed: json['installed'] == true,
        updateAvailable: json['updateAvailable'] == true,
      );
}

class SeededAccount {
  const SeededAccount({required this.username, required this.displayName});
  final String username, displayName;
  factory SeededAccount.fromJson(Map<String, dynamic> json) => SeededAccount(
    username: json['username'] as String,
    displayName: json['displayName'] as String? ?? json['username'] as String,
  );
}
