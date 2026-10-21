/// The contract and change signal behind an app UI kind; the same table AppUiEndpoints keys on.
final class NodeContract {
  const NodeContract(this.contract, this.changed);
  final String contract, changed;
}

abstract final class NodeContracts {
  static const _byKind = <String, NodeContract>{
    'view': NodeContract('IntoChat.OS.Flutter.Views.IView', 'ViewChanged'),
    'scene': NodeContract('IntoChat.OS.Flame.Scene.IScene', 'SceneChanged'),
    'text': NodeContract('IntoChat.OS.Flutter.Text.IText', 'TextChanged'),
    'button': NodeContract(
      'IntoChat.OS.Flutter.Button.IButton',
      'ButtonChanged',
    ),
    'textfield': NodeContract(
      'IntoChat.OS.Flutter.TextField.ITextField',
      'TextFieldChanged',
    ),
    'select': NodeContract(
      'IntoChat.OS.Flutter.Select.ISelect',
      'SelectChanged',
    ),
    'fileinput': NodeContract(
      'IntoChat.OS.Flutter.FileInput.IFileInput',
      'FileInputChanged',
    ),
    'person': NodeContract(
      'IntoChat.OS.Flutter.Person.IPerson',
      'PersonChanged',
    ),
    'card': NodeContract('IntoChat.OS.Flutter.Card.ICard', 'CardChanged'),
    'tabs': NodeContract('IntoChat.OS.Flutter.Tabs.ITabs', 'TabsChanged'),
    'surface': NodeContract(
      'IntoChat.OS.Flutter.Surface.ISurface',
      'SurfaceChanged',
    ),
    'layout': NodeContract(
      'IntoChat.OS.Flutter.Layout.ILayout',
      'LayoutChanged',
    ),
    'collection': NodeContract(
      'IntoChat.OS.Flutter.Collection.ICollectionView',
      'CollectionChanged',
    ),
    'imagecanvas': NodeContract(
      'IntoChat.OS.Flutter.ImageCanvas.IImageCanvas',
      'ImageCanvasChanged',
    ),
    'form': NodeContract('IntoChat.OS.Flutter.Form.IForm', 'FormChanged'),
    'table': NodeContract('IntoChat.OS.Flutter.Table.ITable', 'TableChanged'),
    'chart': NodeContract('IntoChat.OS.Flutter.Chart.IChart', 'ChartChanged'),
    'chat': NodeContract('IntoChat.OS.Flutter.Chat.IChat', 'ChatChanged'),
    'composer': NodeContract(
      'IntoChat.OS.Flutter.Chat.IComposer',
      'ComposerChanged',
    ),
    'voiceinput': NodeContract(
      'IntoChat.OS.Flutter.VoiceInput.IVoiceInput',
      'VoiceInputChanged',
    ),
    'webbrowser': NodeContract(
      'IntoChat.OS.Flutter.WebBrowser.IWebBrowser',
      'BrowserObservationChanged',
    ),
    'viewport': NodeContract(
      'IntoChat.OS.Flutter.Viewport.IViewport',
      'ViewportChanged',
    ),
    'toggle': NodeContract(
      'IntoChat.OS.Flutter.Toggle.IToggle',
      'ToggleChanged',
    ),
    'progress': NodeContract(
      'IntoChat.OS.Flutter.Progress.IProgress',
      'ProgressChanged',
    ),
    'infobar': NodeContract(
      'IntoChat.OS.Flutter.InfoBar.IInfoBar',
      'InfoBarChanged',
    ),
    'image': NodeContract('IntoChat.OS.Flutter.Image.IImage', 'ImageChanged'),
    'expander': NodeContract(
      'IntoChat.OS.Flutter.Expander.IExpander',
      'ExpanderChanged',
    ),
  };

  static NodeContract of(String kind) =>
      _byKind[kind] ??
      (throw ArgumentError.value(kind, 'kind', 'Unknown app UI kind'));
}
