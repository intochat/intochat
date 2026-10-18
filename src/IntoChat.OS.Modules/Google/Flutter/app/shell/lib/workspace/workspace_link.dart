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
  return {
    'kind': 'workspace',
    'application': application,
    'operation': 'open',
    'input': route.queryParameters['input'] ?? '',
  };
}
