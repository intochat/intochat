import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../generated/node_commands.dart';
import '../primitives/primitives.dart';
import '../theme/ui_colors.dart';
import 'node_common.dart';
import 'node_context.dart';

// --- src/nodes/check_box_view.dart ---
Widget renderCheckBoxView(CheckBoxViewNode node, NodeContext ctx) {
  final props = CheckBoxProps.fromJson(ctx.childProps);
  final enabled = props.isEnabled && ctx.interactive;
  // A controlled box draws the parent's value; otherwise the neuron's.
  final isOn = props.isOn ?? node.isOn;
  return Semantics(
    label: controlNameOf(props.automationName, props.label, node.key),
    checked: isOn,
    enabled: enabled,
    child: ExcludeSemantics(
      child: UiMixCheckbox(
        value: isOn,
        label: props.label,
        enabled: enabled,
        onChanged: enabled
            ? (_) => actCheckBoxSetChecked(ctx, '.', isOn: !isOn)
            : null,
      ),
    ),
  );
}

// --- src/nodes/toggle_switch_view.dart ---
Widget renderToggleSwitchView(ToggleSwitchViewNode node, NodeContext ctx) {
  final props = ToggleSwitchProps.fromJson(ctx.childProps);
  // A controlled switch draws the parent's value; otherwise the neuron's.
  final isOn = props.isOn ?? node.isOn;
  final content = isOn ? props.onContent : props.offContent;
  return Row(
    children: [
      Expanded(child: Text(props.label)),
      if (content != null && content.isNotEmpty)
        Padding(padding: const EdgeInsets.only(right: 8), child: Text(content)),
      UiMixToggle(
        value: isOn,
        enabled: props.isEnabled && ctx.interactive,
        semanticLabel: controlNameOf(
          props.automationName,
          props.label,
          node.key,
        ),
        onChanged: props.isEnabled && ctx.interactive
            ? (on) => actToggleSwitchSetChecked(ctx, '.', isOn: on)
            : null,
      ),
    ],
  );
}

// --- src/nodes/expander_view.dart ---
Widget renderExpanderView(ExpanderViewNode node, NodeContext ctx) {
  final props = ExpanderProps.fromJson(ctx.childProps);
  final enabled = props.isEnabled && ctx.interactive;
  return Semantics(
    label: controlNameOf(props.automationName, props.header, node.key),
    button: true,
    enabled: enabled,
    expanded: node.isExpanded,
    child: UiMixExpander(
      header: props.header,
      isExpanded: node.isExpanded,
      enabled: enabled,
      onExpandedChanged: (expanded) =>
          actExpanderExpand(ctx, '.', isExpanded: expanded),
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: renderAll(props.children, ctx),
      ),
    ),
  );
}

// --- src/nodes/tab_strip_view.dart ---
Widget renderTabStripView(TabStripViewNode node, NodeContext ctx) {
  final props = TabStripProps.fromJson(ctx.childProps);
  // A controlled control draws the parent's selection; otherwise the neuron's.
  final selectedId = props.selectedId ?? node.selectedId;
  final enabled = props.isEnabled && ctx.interactive;
  final index = props.items.indexWhere((item) => item.id == selectedId);
  return Semantics(
    label: controlNameOf(props.automationName, '', node.key),
    enabled: enabled,
    child: UiMixTabs(
      labels: [for (final item in props.items) item.label],
      // A selected id that names no tab of the props selects none.
      index: index,
      disabled: enabled
          ? const {}
          : {for (var i = 0; i < props.items.length; i++) i},
      onChanged: (i) {
        if (enabled && i != index) {
          actTabStripSelect(ctx, '.', id: props.items[i].id);
        }
      },
    ),
  );
}

// --- src/nodes/combo_box_view.dart ---
Widget renderComboBoxView(ComboBoxViewNode node, NodeContext ctx) {
  final props = ComboBoxProps.fromJson(ctx.childProps);
  // A controlled control draws the parent's selection; otherwise the neuron's.
  final selectedId = props.selectedId ?? node.selectedId;
  final enabled = props.isEnabled && ctx.interactive;
  final selected = props.options.any((option) => option.id == selectedId)
      ? selectedId
      : null;
  return Semantics(
    label: controlNameOf(props.automationName, props.header, node.key),
    enabled: enabled,
    child: UiMixSelect<String>(
      label: props.header.isEmpty ? null : props.header,
      value: selected,
      items: [
        for (final option in props.options)
          UiMixSelectItem(option.id, option.label),
      ],
      onChanged: enabled
          ? (id) {
              if (id != selected) actComboBoxSelect(ctx, '.', id: id);
            }
          : null,
    ),
  );
}

// --- src/nodes/list_box_view.dart ---
Widget renderListBoxView(ListBoxViewNode node, NodeContext ctx) {
  final props = ListBoxProps.fromJson(ctx.childProps);
  // A controlled control draws the parent's selection; otherwise the neuron's.
  final selectedId = props.selectedId ?? node.selectedId;
  final selectable =
      props.isEnabled &&
      ctx.interactive &&
      props.selectionMode == ListSelectionMode.single;
  return Semantics(
    label: controlNameOf(props.automationName, '', node.key),
    enabled: props.isEnabled,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      mainAxisSize: MainAxisSize.min,
      children: [
        for (final (index, item) in props.items.indexed) ...[
          // A group header where the group changes from the item before.
          if (item.group != null &&
              (index == 0 || props.items[index - 1].group != item.group))
            Padding(
              key: ValueKey('${node.key}/group:$index:${item.group}'),
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
              child: Builder(
                builder: (context) => Text(
                  item.group!,
                  style: Theme.of(context).textTheme.labelSmall,
                ),
              ),
            ),
          // A browser finds an item by its id: `flt-semantics-identifier="item:<id>"`.
          Semantics(
            identifier: 'item:${item.id}',
            container: true,
            explicitChildNodes: true,
            child: ListTile(
              key: ValueKey('${node.key}/${item.id}'),
              dense: true,
              enabled: props.isEnabled,
              selected:
                  props.selectionMode == ListSelectionMode.single &&
                  item.id == selectedId,
              title: Text(item.title),
              subtitle: item.subtitle == null ? null : Text(item.subtitle!),
              onTap: selectable && item.id != selectedId
                  ? () => actListBoxSelect(ctx, '.', id: item.id)
                  : null,
            ),
          ),
        ],
      ],
    ),
  );
}

// --- src/nodes/slider_view.dart ---
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
    final c = UiColors.of(context);
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

// --- src/nodes/form_view.dart ---
Widget renderFormView(FormViewNode node, NodeContext ctx) =>
    _FormView(node: node, ctx: ctx);

class _FormView extends StatefulWidget {
  const _FormView({required this.node, required this.ctx});
  final FormViewNode node;
  final NodeContext ctx;

  @override
  State<_FormView> createState() => _FormViewState();
}

class _FormViewState extends State<_FormView> {
  final _controllers = <String, TextEditingController>{};
  final _typed = <String>{};
  String? _secretError;

  FormProps get _props => FormProps.fromJson(widget.ctx.childProps);

  static bool _isSecret(FormInput field) => field.kind == FormFieldKind.secret;

  // The value drawn until the person types: the parent's, else the last
  // submitted one; a secret field always starts empty.
  String _current(FormInput field) {
    if (_isSecret(field)) return '';
    if (field.value != null) return field.value!;
    for (final value in widget.node.values) {
      if (value.key == field.key) return value.value;
    }
    return '';
  }

  TextEditingController _controller(FormInput field) =>
      _controllers.putIfAbsent(field.key, () {
        final controller = TextEditingController(text: _current(field));
        controller.addListener(() => _typed.add(field.key));
        return controller;
      });

  @override
  void didUpdateWidget(covariant _FormView oldWidget) {
    super.didUpdateWidget(oldWidget);
    // A field the person has not typed in follows the parent's and the neuron's current value.
    for (final field in _props.fields) {
      final controller = _controllers[field.key];
      if (controller == null ||
          _isSecret(field) ||
          _typed.contains(field.key)) {
        continue;
      }
      final current = _current(field);
      if (controller.text != current) {
        controller.text = current;
        _typed.remove(field.key);
      }
    }
  }

  @override
  void dispose() {
    for (final controller in _controllers.values) {
      controller.dispose();
    }
    super.dispose();
  }

  // Stores each typed secret in the person's vault and returns the references
  // by field key; null when a store failed, so nothing is submitted.
  Future<Map<String, String>?> _storeSecrets(List<FormInput> secrets) async {
    final store = widget.ctx.storeSecret;
    final refs = <String, String>{};
    for (final field in secrets) {
      final text = _controller(field).text;
      if (store == null || text.isEmpty) continue;
      try {
        refs[field.key] = await store(
          '${widget.ctx.view}/${field.key}',
          field.label,
          text,
        );
      } on Object {
        return null;
      }
    }
    return refs;
  }

  Future<void> _submit(FormProps props) async {
    final editable = props.fields.where((field) => !field.isReadOnly).toList();
    final secrets = editable.where(_isSecret).toList();
    final secretRefs = await _storeSecrets(secrets);
    if (!mounted) return;
    if (secretRefs == null) {
      setState(() => _secretError = 'Secret not stored: try again.');
      return;
    }
    // A stored secret leaves the screen; only its reference is sent.
    for (final field in secrets) {
      _controller(field).clear();
    }
    setState(() => _secretError = null);
    await actFormSubmit(
      widget.ctx,
      '.',
      values: {
        for (final field in editable)
          if (!_isSecret(field)) field.key: _controller(field).text,
      },
      secretRefs: secretRefs,
    );
    if (!mounted) return;
    // Every field now follows the parent's or the neuron's value again.
    _typed.clear();
  }

  @override
  Widget build(BuildContext context) {
    final props = _props;
    final enabled = props.isEnabled && widget.ctx.interactive;
    return Semantics(
      label: controlNameOf(
        props.automationName,
        props.submitLabel,
        widget.node.key,
      ),
      enabled: enabled,
      container: true,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          for (final field in props.fields)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: UiMixField(
                fieldKey: ValueKey('form_field_${field.key}'),
                label: field.label,
                controller: _controller(field),
                placeholder: field.placeholder,
                multiline: field.kind == FormFieldKind.multiline,
                secret: _isSecret(field),
                error: _isSecret(field) ? _secretError : null,
                keyboardType: field.kind == FormFieldKind.number
                    ? const TextInputType.numberWithOptions(decimal: true)
                    : null,
                enabled:
                    enabled &&
                    !field.isReadOnly &&
                    (!_isSecret(field) || widget.ctx.storeSecret != null),
              ),
            ),
          Align(
            alignment: Alignment.centerLeft,
            child: UiMixButton.preset(
              key: const ValueKey('form_submit'),
              label: props.submitLabel,
              onPressed: enabled ? () => _submit(props) : null,
            ),
          ),
        ],
      ),
    );
  }
}
