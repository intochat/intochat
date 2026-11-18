// This is a generated file - do not edit.
//
// Generated from the control neurons by DartCommandsFacts (IntoChat.OS.Architecture.Tests.Unit).

import '../nodes/node_context.dart';

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

/// Sends `Select` to the `listBox` neuron at [address].
Future<ActivationOutcome> actListBoxSelect(
  NodeContext ctx,
  String address, {
  required String id,
}) => ctx.act(address, 'control.select', {'id': id});

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
