Uri? sameOriginUiBase() {
  final origin = Uri.base.origin;
  if (origin.isEmpty) {
    return null;
  }
  return Uri.parse('$origin/');
}

/// A per-session kernel URL carried in the page address: the test harness and
/// multi-host setups pass it so bundles stay byte-identical and hosts never
/// share a served config file.
String? queryUiBase() {
  final value = Uri.base.queryParameters['uiBase'];
  return value == null || value.isEmpty ? null : value;
}
