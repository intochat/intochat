import 'package:flutter/widgets.dart';

import 'breadcrumb_bar_widget.dart';
import 'chart_widget.dart';
import 'chat_thread_widget.dart';
import 'code_editor_widget.dart';
import 'command_bar_widget.dart';
import 'context_menu_widget.dart';
import 'dialog_widget.dart';
import 'form_widget.dart';
import 'grid_view_widget.dart';
import 'kanban_board_widget.dart';
import 'markdown_viewer_widget.dart';
import 'neuron_widget.dart';
import 'property_grid_widget.dart';
import 'slider_widget.dart';
import 'stepper_widget.dart';
import 'table_widget.dart';
import 'tabs_widget.dart';
import 'tree_view_widget.dart';

/// Returns true when [refKind] names a direct `IntoChat.OS.Modules.UI` neuron.
bool isUiNeuronKind(String refKind) => switch (refKind.trim().toLowerCase()) {
  'ui.chart' ||
  'ui.tabs' ||
  'ui.table' ||
  'ui.form' ||
  'ui.dialog' ||
  'ui.chat' ||
  'ui.slider' ||
  'ui.breadcrumb' ||
  'ui.tree-view' ||
  'ui.context-menu' ||
  'ui.grid-view' ||
  'ui.command-bar' ||
  'ui.code-editor' ||
  'ui.markdown' ||
  'ui.stepper' ||
  'ui.property-grid' ||
  'ui.kanban' => true,
  _ => false,
};

/// Builds the reactive Flutter presenter for any `IntoChat.OS.Modules.UI` neuron.
Widget? buildUiNeuronWidget({
  required String kind,
  required String neuronKey,
  required NeuronMethodCaller callNeuron,
  Stream<void> Function(String kind, String name)? watch,
}) {
  final normalized = kind.trim().toLowerCase();
  return switch (normalized) {
    'ui.chart' => ChartWidget(
      key: ValueKey('ui.chart:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.tabs' => TabsWidget(
      key: ValueKey('ui.tabs:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.table' => TableWidget(
      key: ValueKey('ui.table:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.form' => FormWidget(
      key: ValueKey('ui.form:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.dialog' => DialogWidget(
      key: ValueKey('ui.dialog:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.chat' => ChatThreadWidget(
      key: ValueKey('ui.chat:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.slider' => SliderWidget(
      key: ValueKey('ui.slider:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.breadcrumb' => BreadcrumbBarWidget(
      key: ValueKey('ui.breadcrumb:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.tree-view' => TreeViewWidget(
      key: ValueKey('ui.tree-view:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.context-menu' => ContextMenuNeuronWidget(
      key: ValueKey('ui.context-menu:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.grid-view' => GridViewNeuronWidget(
      key: ValueKey('ui.grid-view:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.command-bar' => CommandBarNeuronWidget(
      key: ValueKey('ui.command-bar:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.code-editor' => CodeEditorNeuronWidget(
      key: ValueKey('ui.code-editor:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.markdown' => MarkdownViewerNeuronWidget(
      key: ValueKey('ui.markdown:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.stepper' => StepperNeuronWidget(
      key: ValueKey('ui.stepper:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.property-grid' => PropertyGridNeuronWidget(
      key: ValueKey('ui.property-grid:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.kanban' => KanbanBoardNeuronWidget(
      key: ValueKey('ui.kanban:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    _ => null,
  };
}
