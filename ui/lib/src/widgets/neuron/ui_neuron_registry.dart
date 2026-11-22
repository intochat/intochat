import 'package:flutter/widgets.dart';

import 'auto_suggest_box_widget.dart';
import 'breadcrumb_bar_widget.dart';
import 'calendar_view_widget.dart';
import 'chart_widget.dart';
import 'chat_thread_widget.dart';
import 'checkbox_widget.dart';
import 'code_editor_widget.dart';
import 'color_picker_widget.dart';
import 'command_bar_widget.dart';
import 'context_menu_widget.dart';
import 'date_picker_widget.dart';
import 'dialog_widget.dart';
import 'expander_widget.dart';
import 'file_picker_widget.dart';
import 'form_widget.dart';
import 'grid_view_widget.dart';
import 'image_viewer_widget.dart';
import 'info_badge_widget.dart';
import 'info_bar_widget.dart';
import 'kanban_board_widget.dart';
import 'listbox_widget.dart';
import 'markdown_viewer_widget.dart';
import 'media_player_widget.dart';
import 'neuron_widget.dart';
import 'notification_toast_widget.dart';
import 'number_box_widget.dart';
import 'person_picture_widget.dart';
import 'pips_pager_widget.dart';
import 'progress_widget.dart';
import 'property_grid_widget.dart';
import 'radio_buttons_widget.dart';
import 'rating_widget.dart';
import 'select_widget.dart';
import 'slider_widget.dart';
import 'split_button_widget.dart';
import 'stepper_widget.dart';
import 'table_widget.dart';
import 'tabs_widget.dart';
import 'teaching_tip_widget.dart';
import 'time_picker_widget.dart';
import 'toggle_widget.dart';
import 'toggle_split_button_widget.dart';
import 'tree_view_widget.dart';

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
  'ui.info-bar' ||
  'ui.date-picker' ||
  'ui.time-picker' ||
  'ui.calendar' ||
  'ui.context-menu' ||
  'ui.grid-view' ||
  'ui.image-viewer' ||
  'ui.media-player' ||
  'ui.command-bar' ||
  'ui.code-editor' ||
  'ui.markdown' ||
  'ui.split-button' ||
  'ui.person-picture' ||
  'ui.teaching-tip' ||
  'ui.stepper' ||
  'ui.property-grid' ||
  'ui.pips-pager' ||
  'ui.info-badge' ||
  'ui.toast' ||
  'ui.file-picker' ||
  'ui.toggle-split-button' ||
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
    'ui.date-picker' => DatePickerNeuronWidget(
      key: ValueKey('ui.date-picker:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.time-picker' => TimePickerNeuronWidget(
      key: ValueKey('ui.time-picker:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.calendar' => CalendarViewNeuronWidget(
      key: ValueKey('ui.calendar:$neuronKey'),
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
    'ui.image-viewer' => ImageViewerNeuronWidget(
      key: ValueKey('ui.image-viewer:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.media-player' => MediaPlayerNeuronWidget(
      key: ValueKey('ui.media-player:$neuronKey'),
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
    'ui.split-button' => SplitButtonNeuronWidget(
      key: ValueKey('ui.split-button:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.person-picture' => PersonPictureNeuronWidget(
      key: ValueKey('ui.person-picture:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.teaching-tip' => TeachingTipNeuronWidget(
      key: ValueKey('ui.teaching-tip:$neuronKey'),
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
    'ui.pips-pager' => PipsPagerNeuronWidget(
      key: ValueKey('ui.pips-pager:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.info-badge' => InfoBadgeNeuronWidget(
      key: ValueKey('ui.info-badge:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.toast' => NotificationToastNeuronWidget(
      key: ValueKey('ui.toast:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.file-picker' => FilePickerNeuronWidget(
      key: ValueKey('ui.file-picker:$neuronKey'),
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
    ),
    'ui.toggle-split-button' => ToggleSplitButtonNeuronWidget(
      key: ValueKey('ui.toggle-split-button:$neuronKey'),
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
