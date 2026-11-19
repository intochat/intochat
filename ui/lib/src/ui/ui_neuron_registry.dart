import 'package:flutter/widgets.dart';

import 'auto_suggest_box/auto_suggest_box_widget.dart';
import 'breadcrumb_bar/breadcrumb_bar_widget.dart';
import 'chart/chart_widget.dart';
import 'chat/chat_thread_widget.dart';
import 'checkbox/checkbox_widget.dart';
import 'color_picker/color_picker_widget.dart';
import 'dialog/dialog_widget.dart';
import 'expander/expander_widget.dart';
import 'form/form_widget.dart';
import 'info_bar/info_bar_widget.dart';
import 'listbox/listbox_widget.dart';
import 'neuron_widget.dart';
import 'number_box/number_box_widget.dart';
import 'progress/progress_widget.dart';
import 'radio_buttons/radio_buttons_widget.dart';
import 'rating/rating_widget.dart';
import 'select/select_widget.dart';
import 'slider/slider_widget.dart';
import 'table/table_widget.dart';
import 'tabs/tabs_widget.dart';
import 'toggle/toggle_widget.dart';
import 'tree_view/tree_view_widget.dart';

/// Returns true when [refKind] names a direct `IntoChat.OS.Modules.UI` neuron.
bool isUiNeuronKind(String refKind) => switch (refKind.trim().toLowerCase()) {
  'ui.chart' ||
  'ui.toggle' ||
  'ui.checkbox' ||
  'ui.select' ||
  'ui.tabs' ||
  'ui.expander' ||
  'ui.listbox' ||
  'ui.table' ||
  'ui.form' ||
  'ui.dialog' ||
  'ui.chat' ||
  'ui.slider' ||
  'ui.radio-buttons' ||
  'ui.number-box' ||
  'ui.rating' ||
  'ui.color-picker' ||
  'ui.breadcrumb' ||
  'ui.auto-suggest' ||
  'ui.tree-view' ||
  'ui.progress' ||
  'ui.info-bar' => true,
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
    'ui.toggle' => ToggleWidget(
      key: ValueKey('ui.toggle:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.checkbox' => CheckBoxWidget(
      key: ValueKey('ui.checkbox:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.select' => SelectWidget(
      key: ValueKey('ui.select:$neuronKey'),
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
    'ui.expander' => ExpanderWidget(
      key: ValueKey('ui.expander:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.listbox' => ListBoxWidget(
      key: ValueKey('ui.listbox:$neuronKey'),
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
    'ui.radio-buttons' => RadioButtonsWidget(
      key: ValueKey('ui.radio-buttons:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.number-box' => NumberBoxWidget(
      key: ValueKey('ui.number-box:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.rating' => RatingWidget(
      key: ValueKey('ui.rating:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.color-picker' => ColorPickerWidget(
      key: ValueKey('ui.color-picker:$neuronKey'),
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
    'ui.auto-suggest' => AutoSuggestBoxWidget(
      key: ValueKey('ui.auto-suggest:$neuronKey'),
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
    'ui.progress' => ProgressWidget(
      key: ValueKey('ui.progress:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.info-bar' => InfoBarWidget(
      key: ValueKey('ui.info-bar:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    _ => null,
  };
}
