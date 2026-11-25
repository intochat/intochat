/// The contract and change signal behind an app UI kind; the same table AppUiEndpoints keys on.
final class NodeContract {
  const NodeContract(this.contract, this.changed);

  /// A shell-watched source that is not an app UI kind, so it stays out of the UI kind table.
  const NodeContract.source(this.contract, this.changed);
  final String contract, changed;
}

abstract final class NodeContracts {
  static const _byKind = <String, NodeContract>{
    'confirmation': NodeContract(
      'DigitalBrain.Confirmation.IConfirmationInbox',
      'ConfirmationRequested',
    ),
    'view': NodeContract(
      'IntoChat.OS.Modules.Flutter.Views.IView',
      'FlutterViewChanged',
    ),
    'webbrowser': NodeContract(
      'IntoChat.OS.Modules.Flutter.WebBrowser.IWebBrowser',
      'BrowserObservationChanged',
    ),
    'viewport': NodeContract(
      'IntoChat.OS.Modules.Flutter.Viewport.IViewport',
      'ViewportChanged',
    ),
  };

  // The shell's own watches (friends' space, Ino's presence, the user settings); not UI kinds a view may reference.
  static const _shellSources = <String, NodeContract>{
    // A friend's profile, posts and post cards change with the brain's space scene.
    'friends.space': NodeContract.source(
      'IntoChat.OS.Modules.Flame.Scene.IScene',
      'SceneChanged',
    ),
    'ino.presence': NodeContract.source(
      'IntoChat.OS.Modules.Apps.IPresence',
      'PresenceChanged',
    ),
    'os.settings': NodeContract.source(
      'IntoChat.OS.Modules.Apps.IUserSettings',
      'UserSettingsChanged',
    ),
  };

  static NodeContract? tryResolve(String kind) =>
      _byKind[kind] ?? _shellSources[kind];

  static NodeContract of(String kind) =>
      tryResolve(kind) ??
      (throw ArgumentError.value(kind, 'kind', 'Unknown app UI kind'));
}
