// This is a generated file - do not edit.
//
// Generated from the control neurons by DartCommandsFacts (IntoChat.OS.Architecture.Tests.Unit).

import '../nodes/node_context.dart';

/// Sends `AppendMessage` to the `chatThread` neuron at [address].
Future<ActivationOutcome> actChatThreadAppendMessage(
  NodeContext ctx,
  String address, {
  required Map<String, Object?> message,
}) => ctx.act(address, 'control.appendMessage', {'message': message});

/// Sends `ImportMessages` to the `chatThread` neuron at [address].
Future<ActivationOutcome> actChatThreadImportMessages(
  NodeContext ctx,
  String address, {
  required List<Map<String, Object?>> messages,
  required bool replace,
}) => ctx.act(address, 'control.importMessages', {
  'messages': messages,
  'replace': replace,
});

/// Sends `Retry` to the `chatThread` neuron at [address].
Future<ActivationOutcome> actChatThreadRetry(
  NodeContext ctx,
  String address, {
  required String id,
}) => ctx.act(address, 'control.retry', {'id': id});

/// Sends `SelectModel` to the `chatThread` neuron at [address].
Future<ActivationOutcome> actChatThreadSelectModel(
  NodeContext ctx,
  String address, {
  required String? value,
  required bool defaultValue,
}) => ctx.act(address, 'control.selectModel', {
  'value': value,
  'default': defaultValue,
});

/// Sends `Stop` to the `chatThread` neuron at [address].
Future<ActivationOutcome> actChatThreadStop(NodeContext ctx, String address) =>
    ctx.act(address, 'control.stop', {});

/// Sends `SubmitText` to the `chatThread` neuron at [address].
Future<ActivationOutcome> actChatThreadSubmitText(
  NodeContext ctx,
  String address, {
  required String? value,
}) => ctx.act(address, 'control.submitText', {'value': value});

/// Sends `UpdateMessage` to the `chatThread` neuron at [address].
Future<ActivationOutcome> actChatThreadUpdateMessage(
  NodeContext ctx,
  String address, {
  required String id,
  required Map<String, Object?> patch,
}) => ctx.act(address, 'control.updateMessage', {'id': id, 'patch': patch});

/// Sends `SetChecked` to the `checkBox` neuron at [address].
Future<ActivationOutcome> actCheckBoxSetChecked(
  NodeContext ctx,
  String address, {
  required bool isOn,
}) => ctx.act(address, 'control.setChecked', {'isOn': isOn});

/// Sends `Toggle` to the `checkBox` neuron at [address].
Future<ActivationOutcome> actCheckBoxToggle(NodeContext ctx, String address) =>
    ctx.act(address, 'control.toggle', {});

/// Sends `Select` to the `comboBox` neuron at [address].
Future<ActivationOutcome> actComboBoxSelect(
  NodeContext ctx,
  String address, {
  required String id,
}) => ctx.act(address, 'control.select', {'id': id});

/// Sends `Expand` to the `expander` neuron at [address].
Future<ActivationOutcome> actExpanderExpand(
  NodeContext ctx,
  String address, {
  required bool isExpanded,
}) => ctx.act(address, 'control.expand', {'isExpanded': isExpanded});

/// Sends `Submit` to the `form` neuron at [address].
Future<ActivationOutcome> actFormSubmit(
  NodeContext ctx,
  String address, {
  required Map<String, String> values,
  required Map<String, String> secretRefs,
}) => ctx.act(address, 'control.submit', {
  'values': values,
  'secretRefs': secretRefs,
});

/// Sends `Select` to the `listBox` neuron at [address].
Future<ActivationOutcome> actListBoxSelect(
  NodeContext ctx,
  String address, {
  required String id,
}) => ctx.act(address, 'control.select', {'id': id});

/// Sends `Append` to the `liveChart` neuron at [address].
Future<ActivationOutcome> actLiveChartAppend(
  NodeContext ctx,
  String address, {
  required Map<String, Object?> point,
}) => ctx.act(address, 'control.append', {'point': point});

/// Sends `Replace` to the `liveChart` neuron at [address].
Future<ActivationOutcome> actLiveChartReplace(
  NodeContext ctx,
  String address, {
  required List<Map<String, Object?>> points,
}) => ctx.act(address, 'control.replace', {'points': points});

/// Sends `Filter` to the `queryTable` neuron at [address].
Future<ActivationOutcome> actQueryTableFilter(
  NodeContext ctx,
  String address, {
  required String text,
}) => ctx.act(address, 'control.filter', {'text': text});

/// Sends `Page` to the `queryTable` neuron at [address].
Future<ActivationOutcome> actQueryTablePage(
  NodeContext ctx,
  String address, {
  required int number,
}) => ctx.act(address, 'control.page', {'number': number});

/// Sends `SelectRow` to the `queryTable` neuron at [address].
Future<ActivationOutcome> actQueryTableSelectRow(
  NodeContext ctx,
  String address, {
  required String rowId,
}) => ctx.act(address, 'control.selectRow', {'rowId': rowId});

/// Sends `Sort` to the `queryTable` neuron at [address].
Future<ActivationOutcome> actQueryTableSort(
  NodeContext ctx,
  String address, {
  required String column,
}) => ctx.act(address, 'control.sort', {'column': column});

/// Sends `Select` to the `tabStrip` neuron at [address].
Future<ActivationOutcome> actTabStripSelect(
  NodeContext ctx,
  String address, {
  required String id,
}) => ctx.act(address, 'control.select', {'id': id});

/// Sends `SetChecked` to the `toggleSwitch` neuron at [address].
Future<ActivationOutcome> actToggleSwitchSetChecked(
  NodeContext ctx,
  String address, {
  required bool isOn,
}) => ctx.act(address, 'control.setChecked', {'isOn': isOn});

/// Sends `Toggle` to the `toggleSwitch` neuron at [address].
Future<ActivationOutcome> actToggleSwitchToggle(
  NodeContext ctx,
  String address,
) => ctx.act(address, 'control.toggle', {});
