/// Canonical ArtCraft studio IDs mapped to their default local bridge web ports.
const Map<String, int> artCraftDefaultWebPorts = {
  'photocraft': 18082,
  'filmcraft': 18083,
  'vectorcraft': 18084,
  'lightcraft': 18085,
  'effectcraft': 18086,
  'designcraft': 18087,
  'pdfcraft': 18088,
  'wordcraft': 18089,
  'soundcraft': 18090,
  'deckcraft': 18091,
  'cadcraft': 18092,
  'gridcraft': 18093,
  'figcraft': 18094,
  'texturecraft': 18095,
  'materialcraft': 18096,
  'solidcraft': 18097,
  // Kept last so `/artcraft/<studio>/index.html` matches specific studio IDs first.
  'artcraft': 18080,
};

/// Returns `true` when [rawAppId] identifies one of the 17 ArtCraft studios
/// (accepting `artcraft:<id>` prefixes and the legacy `videocraft` alias).
bool isArtCraftApp(String? rawAppId) {
  if (rawAppId == null || rawAppId.trim().isEmpty) return false;
  var norm = rawAppId.trim().toLowerCase();
  if (norm.startsWith('artcraft:')) {
    norm = norm.substring('artcraft:'.length);
  }
  if (norm == 'videocraft') norm = 'filmcraft';
  return artCraftDefaultWebPorts.containsKey(norm);
}

/// Returns the canonical ArtCraft studio ID matched by studio name, legacy
/// alias (`videocraft`), or localhost bridge port (`18080..18097`, `18110`,
/// `18111`), or `null` when [raw] does not refer to an ArtCraft studio.
String? findArtCraftByUrlOrName(String? raw) {
  if (raw == null || raw.trim().isEmpty) return null;
  final lower = raw.toLowerCase();
  if (lower.contains('18110')) return 'photocraft';
  if (lower.contains('18111') || lower.contains('videocraft')) {
    return 'filmcraft';
  }
  for (final entry in artCraftDefaultWebPorts.entries) {
    if (lower.contains(entry.key) || lower.contains('${entry.value}')) {
      return entry.key;
    }
  }
  return null;
}

/// Resolves a studio WebBrowser URL to the same-origin Kernel `/artcraft/<id>/index.html`
/// endpoint when running on Web (`isWeb == true`) and the raw URL points to localhost or
/// a relative `/artcraft/` path.
String resolveArtCraftWebUrl(
  String rawUrl, {
  String? hint,
  required bool isWeb,
  Uri? baseUri,
  String? uiBaseOrigin,
}) {
  final trimmed = rawUrl.trim();
  if (!isWeb) return trimmed;
  final base = baseUri ?? Uri.base;
  if (base.scheme != 'http' && base.scheme != 'https') return trimmed;

  final craftId = findArtCraftByUrlOrName('${hint ?? ''} $trimmed');
  if (craftId == null) return trimmed;

  final isLocalOrEmpty =
      trimmed.isEmpty ||
      trimmed.startsWith('/artcraft/') ||
      trimmed.contains('localhost') ||
      trimmed.contains('127.0.0.1');
  if (!isLocalOrEmpty) return trimmed;

  var origin = base.origin;
  if (uiBaseOrigin != null && uiBaseOrigin.isNotEmpty) {
    final parsed = Uri.tryParse(uiBaseOrigin);
    if (parsed != null && parsed.hasAuthority) {
      origin = parsed.origin;
    }
  }
  final parsedRaw = Uri.tryParse(trimmed);
  final docQuery = parsedRaw?.queryParameters['doc'];
  if (docQuery != null && docQuery.isNotEmpty) {
    return '$origin/artcraft/$craftId/index.html?doc=${Uri.encodeQueryComponent(docQuery)}';
  }
  return '$origin/artcraft/$craftId/index.html';
}
