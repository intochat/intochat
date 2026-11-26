import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../theme/ui_tokens.dart';
import '../generated/node_commands.dart';
import '../primitives/slider.dart';
import 'node_context.dart';

/// What a slider neuron draws: the value from the neuron's view; the label,
/// range, step and unit from the child's props. The value is the props' when
/// the parent owns it, else the neuron's, else the props' minimum. While the
/// person drags, the slider draws the dragged value and ignores the props;
/// on release it sends one `SetValue` to the neuron itself (`.`), and the
/// parent checks the range. The released value stays drawn until the next
/// view or props arrive, or until the command is refused. Disabled props send
/// nothing.
Widget renderSliderView(SliderViewNode node, NodeContext ctx) =>
    _SliderView(key: ValueKey('slider:${node.key}'), node: node, ctx: ctx);

class _SliderView extends StatefulWidget {
  const _SliderView({super.key, required this.node, required this.ctx});

  final SliderViewNode node;
  final NodeContext ctx;

  @override
  State<_SliderView> createState() => _SliderViewState();
}

class _SliderViewState extends State<_SliderView> {
  // The dragged (then released) value, drawn instead of the props or the
  // neuron until the next view or props arrive.
  double? _dragged;

  @override
  void didUpdateWidget(_SliderView old) {
    super.didUpdateWidget(old);
    if (!_dragging) _dragged = null;
  }

  bool _dragging = false;

  @override
  Widget build(BuildContext context) {
    final props = SliderProps.fromJson(widget.ctx.childProps);
    final enabled = props.isEnabled && widget.ctx.interactive;
    final max = props.max > props.min ? props.max : props.min + 1;
    final step = props.step > 0 ? props.step : (max - props.min) / 100;
    final value = (_dragged ?? props.value ?? widget.node.value ?? props.min)
        .clamp(props.min, max)
        .toDouble();
    final c = IcTheme.of(context);
    return Semantics(
      container: true,
      explicitChildNodes: true,
      label: controlNameOf(props.automationName, props.label, widget.node.key),
      enabled: enabled,
      value: '${_format(value)}${props.unit}',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            children: [
              if (props.label.isNotEmpty)
                Expanded(
                  child: Text(
                    props.label,
                    style: TextStyle(
                      fontSize: 13.5,
                      fontWeight: FontWeight.w500,
                      color: enabled ? c.textStrong : c.textMuted,
                    ),
                  ),
                ),
              Text(
                '${_format(value)}${props.unit}',
                style: TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  color: c.accent,
                  fontFeatures: const [FontFeature.tabularFigures()],
                ),
              ),
            ],
          ),
          UiMixSlider(
            value: value,
            min: props.min,
            max: max,
            step: step,
            enabled: enabled,
            semanticsId: widget.node.key,
            onChanged: enabled
                ? (v) => setState(() {
                    _dragging = true;
                    _dragged = v;
                  })
                : null,
            onChangeEnd: enabled ? _release : null,
          ),
        ],
      ),
    );
  }

  Future<void> _release(double value) async {
    setState(() {
      _dragging = false;
      _dragged = value;
    });
    final outcome = await actSliderSetValue(widget.ctx, '.', value: value);
    if (outcome != ActivationOutcome.accepted && mounted) {
      setState(() => _dragged = null);
    }
  }

  static String _format(double value) => value == value.roundToDouble()
      ? '${value.round()}'
      : value.toStringAsFixed(1);
}
