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
      'ViewChanged',
    ),
    'scene': NodeContract(
      'IntoChat.OS.Modules.Flame.Scene.IScene',
      'SceneChanged',
    ),
    'text': NodeContract(
      'IntoChat.OS.Modules.Flutter.Text.IText',
      'TextChanged',
    ),
    'button': NodeContract(
      'IntoChat.OS.Modules.Flutter.Button.IButton',
      'ButtonChanged',
    ),
    'textfield': NodeContract(
      'IntoChat.OS.Modules.Flutter.TextField.ITextField',
      'TextFieldChanged',
    ),
    'select': NodeContract(
      'IntoChat.OS.Modules.Flutter.Select.ISelect',
      'SelectChanged',
    ),
    'fileinput': NodeContract(
      'IntoChat.OS.Modules.Flutter.FileInput.IFileInput',
      'FileInputChanged',
    ),
    'person': NodeContract(
      'IntoChat.OS.Modules.Flutter.Person.IPerson',
      'PersonChanged',
    ),
    'card': NodeContract(
      'IntoChat.OS.Modules.Flutter.Card.ICard',
      'CardChanged',
    ),
    'tabs': NodeContract(
      'IntoChat.OS.Modules.Flutter.Tabs.ITabs',
      'TabsChanged',
    ),
    'surface': NodeContract(
      'IntoChat.OS.Modules.Flutter.Surface.ISurface',
      'SurfaceChanged',
    ),
    'layout': NodeContract(
      'IntoChat.OS.Modules.Flutter.Layout.ILayout',
      'LayoutChanged',
    ),
    'collection': NodeContract(
      'IntoChat.OS.Modules.Flutter.Collection.ICollectionView',
      'CollectionChanged',
    ),
    'imagecanvas': NodeContract(
      'IntoChat.OS.Modules.Flutter.ImageCanvas.IImageCanvas',
      'ImageCanvasChanged',
    ),
    'form': NodeContract(
      'IntoChat.OS.Modules.Flutter.Form.IForm',
      'FormChanged',
    ),
    'table': NodeContract(
      'IntoChat.OS.Modules.Flutter.Table.ITable',
      'TableChanged',
    ),
    'chart': NodeContract(
      'IntoChat.OS.Modules.Flutter.Chart.IChart',
      'ChartChanged',
    ),
    'chat': NodeContract(
      'IntoChat.OS.Modules.Flutter.Chat.IChat',
      'ChatChanged',
    ),
    'composer': NodeContract(
      'IntoChat.OS.Modules.Flutter.Chat.IComposer',
      'ComposerChanged',
    ),
    'voiceinput': NodeContract(
      'IntoChat.OS.Modules.Flutter.VoiceInput.IVoiceInput',
      'VoiceInputChanged',
    ),
    'webbrowser': NodeContract(
      'IntoChat.OS.Modules.Flutter.WebBrowser.IWebBrowser',
      'BrowserObservationChanged',
    ),
    'viewport': NodeContract(
      'IntoChat.OS.Modules.Flutter.Viewport.IViewport',
      'ViewportChanged',
    ),
    'toggle': NodeContract(
      'IntoChat.OS.Modules.Flutter.Toggle.IToggle',
      'ToggleChanged',
    ),
    'entrycontrol': NodeContract(
      'IntoChat.OS.Modules.Flutter.Entries.IEntryControl',
      'EntryControlChanged',
    ),
    'preference': NodeContract(
      'IntoChat.OS.Modules.Flutter.Preferences.IPreferenceControl',
      'PreferenceChanged',
    ),
    'infobar': NodeContract(
      'IntoChat.OS.Modules.Flutter.InfoBar.IInfoBar',
      'InfoBarChanged',
    ),
    'image': NodeContract(
      'IntoChat.OS.Modules.Flutter.Image.IImage',
      'ImageChanged',
    ),
    'expander': NodeContract(
      'IntoChat.OS.Modules.Flutter.Expander.IExpander',
      'ExpanderChanged',
    ),
  };

  // The shell's own watches (Ino's presence, the user settings); not UI kinds a view may reference.
  static const _shellSources = <String, NodeContract>{
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
