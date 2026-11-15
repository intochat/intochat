import 'dart:async';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_field.dart';
import 'node_context.dart';

Widget renderField(FieldNode node, NodeContext ctx) => FieldNodeView(
  key: ValueKey('field:${ctx.fieldName(node.name)}'),
  node: node,
  ctx: ctx,
);

/// A view field bound to its field neuron: the value is read by `kind=field`,
/// re-read on `FieldChanged`, and each edit is posted to the neuron. A secret
/// never shows what is stored. With `onSubmit` a submit activates the field's
/// key on the view with the typed text.
class FieldNodeView extends StatefulWidget {
  const FieldNodeView({super.key, required this.node, required this.ctx});
  final FieldNode node;
  final NodeContext ctx;

  @override
  State<FieldNodeView> createState() => _FieldNodeViewState();
}

class _FieldNodeViewState extends State<FieldNodeView> {
  final _controller = TextEditingController();
  final _focus = FocusNode();
  StreamSubscription<void>? _watch;
  Timer? _debounce;
  bool _dirty = false;
  // Each posted edit's number: only the latest response clears [_dirty].
  int _sent = 0;

  static const debounce = Duration(milliseconds: 250);

  bool get _secret =>
      widget.node.fieldKind == FieldKind.secret ||
      widget.node.fieldKind == FieldKind.password;

  String get _name => widget.ctx.fieldName(widget.node.name);

  @override
  void initState() {
    super.initState();
    _read();
    _watch = widget.ctx.watch
        ?.call('field', _name)
        .listen((_) => _read(), onError: (Object _) {});
  }

  Future<void> _read() async {
    final load = widget.ctx.load;
    if (load == null || _secret) return;
    try {
      final state = await load('field', _name);
      if (!mounted || _dirty || _focus.hasFocus) return;
      final value = state['value'];
      if (value is String && value != _controller.text) {
        _controller.text = value;
      }
    } catch (_) {
      // The field stays as typed; the next change signal reads again.
    }
  }

  void _changed(String value) {
    _dirty = true;
    _debounce?.cancel();
    _debounce = Timer(debounce, _post);
  }

  // Posts the text as typed now; the event queue keeps it before any later activation.
  void _post() {
    _debounce?.cancel();
    _debounce = null;
    final number = ++_sent;
    unawaited(
      widget.ctx
          .input(widget.node.name, _controller.text)
          .whenComplete(() {
            if (number == _sent) _dirty = false;
          })
          .catchError((Object _) {}),
    );
  }

  void _submitted(String value) {
    if (widget.node.onSubmit == null) return;
    if (_debounce != null) _post();
    unawaited(
      widget.ctx
          .activate(widget.node.key, value: value)
          .catchError((Object _) {}),
    );
    if (_secret) _controller.clear();
  }

  @override
  void dispose() {
    // An edit still waiting out its debounce is sent, not lost.
    if (_debounce != null) _post();
    _watch?.cancel();
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final node = widget.node;
    final label = node.name.contains('/')
        ? node.name.substring(node.name.lastIndexOf('/') + 1)
        : node.name;
    final clearable =
        node.fieldKind == FieldKind.search ||
        node.fieldKind == FieldKind.text ||
        node.fieldKind == FieldKind.suggest;
    return UiKitField(
      label: label,
      controller: _controller,
      focusNode: _focus,
      placeholder: node.placeholder,
      secret: _secret,
      clearable: clearable,
      leadingIcon: node.fieldKind == FieldKind.search ? 'search' : null,
      keyboardType: node.fieldKind == FieldKind.number
          ? const TextInputType.numberWithOptions(decimal: true, signed: true)
          : null,
      autocorrect: !_secret && node.fieldKind != FieldKind.search,
      multiline: node.fieldKind == FieldKind.multiline,
      enabled: widget.ctx.interactive,
      onChanged: _changed,
      onSubmitted: node.onSubmit == null ? null : _submitted,
    );
  }
}
