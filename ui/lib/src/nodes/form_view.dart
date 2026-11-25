import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../primitives/button.dart';
import '../primitives/field.dart';
import '../generated/node_commands.dart';
import 'node_context.dart';

/// What a form neuron draws: the fields, submit label, enabled and accessible
/// name from the child's props; the last submitted values from the neuron's view.
/// Typed text stays here until Submit, which sends every editable field's
/// value at once (typed or not) to the neuron itself (`.`). Read-only fields
/// are drawn but not sent. A secret field's typed text goes only to the
/// person's vault ([NodeContext.storeSecret]); Submit carries the returned
/// reference in `secretRefs`, never the text, and the field is cleared once
/// stored. Without a secret store the secret field is drawn disabled.
/// Disabled props send nothing.
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
              child: UiKitField(
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
