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
    'field': NodeContract(
      'IntoChat.OS.Modules.Flutter.Fields.IField',
      'FieldChanged',
    ),
    'webbrowser': NodeContract(
      'IntoChat.OS.Modules.Flutter.WebBrowser.IWebBrowser',
      'BrowserObservationChanged',
    ),
    'viewport': NodeContract(
      'IntoChat.OS.Modules.Flutter.Viewport.IViewport',
      'ViewportChanged',
    ),
    // Unified DDD UI Neuron Kit (IntoChat.OS.Modules.UI)
    'ui.chart': NodeContract(
      'IntoChat.OS.Modules.UI.Chart.IChart',
      'UiChartChanged',
    ),
    'ui.tabs': NodeContract('IntoChat.OS.Modules.UI.Tabs.ITabs', 'TabChanged'),
    'ui.table': NodeContract(
      'IntoChat.OS.Modules.UI.Table.ITable',
      'UiTableChanged',
    ),
    'ui.form': NodeContract('IntoChat.OS.Modules.UI.Form.IForm', 'FormChanged'),
    'ui.dialog': NodeContract(
      'IntoChat.OS.Modules.UI.Dialog.IDialog',
      'DialogChanged',
    ),
    'ui.chat': NodeContract(
      'IntoChat.OS.Modules.UI.Chat.IChatThread',
      'ChatThreadChanged',
    ),
    'ui.slider': NodeContract(
      'IntoChat.OS.Modules.UI.Slider.ISlider',
      'SliderChanged',
    ),
    'ui.breadcrumb': NodeContract(
      'IntoChat.OS.Modules.UI.BreadcrumbBar.IBreadcrumbBar',
      'BreadcrumbClicked',
    ),
    'ui.tree-view': NodeContract(
      'IntoChat.OS.Modules.UI.TreeView.ITreeView',
      'TreeViewChanged',
    ),
    'ui.context-menu': NodeContract(
      'IntoChat.OS.Modules.UI.ContextMenu.IContextMenu',
      'ContextMenuInvoked',
    ),
    'ui.grid-view': NodeContract(
      'IntoChat.OS.Modules.UI.GridView.IGridView',
      'GridViewChanged',
    ),
    'ui.image-viewer': NodeContract(
      'IntoChat.OS.Modules.UI.ImageViewer.IImageViewer',
      'ImageViewerChanged',
    ),
    'ui.command-bar': NodeContract(
      'IntoChat.OS.Modules.UI.CommandBar.ICommandBar',
      'CommandBarInvoked',
    ),
    'ui.code-editor': NodeContract(
      'IntoChat.OS.Modules.UI.CodeEditor.ICodeEditor',
      'CodeEditorChanged',
    ),
    'ui.markdown': NodeContract(
      'IntoChat.OS.Modules.UI.MarkdownViewer.IMarkdownViewer',
      'MarkdownChanged',
    ),
    'ui.stepper': NodeContract(
      'IntoChat.OS.Modules.UI.Stepper.IStepper',
      'StepperChanged',
    ),
    'ui.property-grid': NodeContract(
      'IntoChat.OS.Modules.UI.PropertyGrid.IPropertyGrid',
      'PropertyGridChanged',
    ),
    'ui.kanban': NodeContract(
      'IntoChat.OS.Modules.UI.KanbanBoard.IKanbanBoard',
      'KanbanBoardChanged',
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
