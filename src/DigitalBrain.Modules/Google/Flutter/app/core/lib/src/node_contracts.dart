/// The contract and change signal behind an app UI kind; the same table AppUiEndpoints keys on.
final class NodeContract {
  const NodeContract(this.contract, this.changed);
  final String contract, changed;
}

abstract final class NodeContracts {
  static const _byKind = <String, NodeContract>{
    'scene': NodeContract('DigitalBrain.Flame.Scene.IScene', 'SceneChanged'),
    'text': NodeContract('DigitalBrain.Flutter.Text.IText', 'TextChanged'),
    'button': NodeContract(
      'DigitalBrain.Flutter.Button.IButton',
      'ButtonChanged',
    ),
    'textfield': NodeContract(
      'DigitalBrain.Flutter.TextField.ITextField',
      'TextFieldChanged',
    ),
    'select': NodeContract(
      'DigitalBrain.Flutter.Select.ISelect',
      'SelectChanged',
    ),
    'fileinput': NodeContract(
      'DigitalBrain.Flutter.FileInput.IFileInput',
      'FileInputChanged',
    ),
    'card': NodeContract('DigitalBrain.Flutter.Card.ICard', 'CardChanged'),
    'tabs': NodeContract('DigitalBrain.Flutter.Tabs.ITabs', 'TabsChanged'),
    'surface': NodeContract(
      'DigitalBrain.Flutter.Surface.ISurface',
      'SurfaceChanged',
    ),
    'layout': NodeContract(
      'DigitalBrain.Flutter.Layout.ILayout',
      'LayoutChanged',
    ),
    'collection': NodeContract(
      'DigitalBrain.Flutter.Collection.ICollectionView',
      'CollectionChanged',
    ),
    'imagecanvas': NodeContract(
      'DigitalBrain.Flutter.ImageCanvas.IImageCanvas',
      'ImageCanvasChanged',
    ),
    'form': NodeContract('DigitalBrain.Flutter.Form.IForm', 'FormChanged'),
    'table': NodeContract('DigitalBrain.Flutter.Table.ITable', 'TableChanged'),
    'chart': NodeContract('DigitalBrain.Flutter.Chart.IChart', 'ChartChanged'),
    'chat': NodeContract('DigitalBrain.Flutter.Chat.IChat', 'ChatChanged'),
    'composer': NodeContract(
      'DigitalBrain.Flutter.Chat.IComposer',
      'ComposerChanged',
    ),
    'voiceinput': NodeContract(
      'DigitalBrain.Flutter.VoiceInput.IVoiceInput',
      'VoiceInputChanged',
    ),
    'webbrowser': NodeContract(
      'DigitalBrain.Flutter.WebBrowser.IWebBrowser',
      'BrowserObservationChanged',
    ),
  };

  static NodeContract of(String kind) =>
      _byKind[kind] ??
      (throw ArgumentError.value(kind, 'kind', 'Unknown app UI kind'));
}
