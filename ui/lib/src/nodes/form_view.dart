import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_button.dart';
import '../components/kit/ui_kit_field.dart';
import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a form neuron draws: the fields, submit label, enabled and accessible
/// name from the slot's props; the last submitted values from the neuron's view.
/// Typed text stays here until Submit, which sends every editable field's
/// value at once (typed or not) to the neuron itself (`.`). Read-only fields
/// are drawn but not sent. Secret fields are not supported yet and are not
/// drawn (the neuron's parent refuses a form that has one). Disabled props
/// send nothing.
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

  FormProps get _props => FormProps.fromJson(widget.ctx.slotProps);

  // The value drawn until the person types: the parent's, else the last submitted one.
  String _current(FormInput field) {
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
      if (controller == null || _typed.contains(field.key)) continue;
      final current = _current(field);
      if (controller.text != current) {
        controller.text = current;
        _typed.remove(field.key);
      }
    }
  }

  static Iterable<FormInput> _offered(FormProps props) =>
      props.fields.where((field) => field.kind != FormFieldKind.secret);

  @override
  void dispose() {
    for (final controller in _controllers.values) {
      controller.dispose();
    }
    super.dispose();
  }

  Future<void> _submit(FormProps props) async {
    final editable = _offered(props)
        .where((field) => !field.isReadOnly)
        .toList();
    await actFormSubmit(
      widget.ctx,
      '.',
      values: {
        for (final field in editable) field.key: _controller(field).text,
      },
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
          for (final field in _offered(props))
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: UiKitField(
                fieldKey: ValueKey('form_field_${field.key}'),
                label: field.label,
                controller: _controller(field),
                placeholder: field.placeholder,
                multiline: field.kind == FormFieldKind.multiline,
                keyboardType: field.kind == FormFieldKind.number
                    ? const TextInputType.numberWithOptions(decimal: true)
                    : null,
                enabled: enabled && !field.isReadOnly,
              ),
            ),
          Align(
            alignment: Alignment.centerLeft,
            child: UiKitButton(
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
