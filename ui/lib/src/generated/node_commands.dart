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
