// External links open an app in the signed-in account's selected brain.
// App-owned input stays opaque to the connector.
Map<String, dynamic>? workspaceLink(Uri? location) {
  if (location == null) return null;
  final route = location.fragment.startsWith('/')
      ? Uri.tryParse(location.fragment)
      : location;
  if (route == null || route.path != '/open') return null;
  final application = route.queryParameters['application'];
  if (application == null ||
      !RegExp(r'^[a-z0-9][a-z0-9-]{0,79}$').hasMatch(application)) {
    return null;
  }
  final input = route.queryParameters['input'] ?? '';
  if (input.length > 2048) return null;
  final publisher = route.queryParameters['publisher'];
  final release = route.queryParameters['release'];
  if (publisher == null) {
    if (release != null) return null;
    return {
      'kind': 'workspace',
      'application': application,
      'operation': 'open',
      'input': input,
    };
  }
  if (!RegExp(r'^[a-z0-9][a-z0-9-]{0,63}$').hasMatch(publisher)) return null;
  if (release != null && !RegExp(r'^\d+\.\d+\.\d+$').hasMatch(release)) {
    return null;
  }
  // A shared app link opens that app's detail in Apps; the input is not carried.
  return {
    'kind': 'workspace',
    'application': 'apps',
    'operation': 'open',
    'input': 'app:$publisher/$application${release == null ? '' : '@$release'}',
  };
}
