/// The app keys the shell can open a window for: the brain's installed apps
/// reported by the desktop catalog and apps endpoint. A view's `OpenWindow`
/// intent for any other app is refused by the intent gate.
Set<String> knownAppsFor(Iterable<String> installed) => {
  for (final app in installed)
    if (app.trim().isNotEmpty) app.trim().toLowerCase(),
};
