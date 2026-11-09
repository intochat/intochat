import '../desktop/smart_desktop.dart' show defaultSmartAppSpecs;

/// The app keys the shell can open a window for: the shell's own launcher apps
/// and the brain's installed apps. A view's `OpenWindow` intent for any other
/// app is refused by the intent gate.
Set<String> knownAppsFor(Iterable<String> installed) => {
  for (final spec in defaultSmartAppSpecs) spec.id,
  for (final app in installed)
    if (app.trim().isNotEmpty) app.trim().toLowerCase(),
};
