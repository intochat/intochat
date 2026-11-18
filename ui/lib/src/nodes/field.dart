import 'dart:async';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_field.dart';
import '../theme/wire_node_values.dart';
import 'node_context.dart';

String _humanizeFieldPath(String rawName) => humanizeFieldLabel(rawName);

Map<String, dynamic> _fieldMeta(FieldNode node) {
  final args = node.onSubmit?.args;
  return args is Map ? Map<String, dynamic>.from(args) : const {};
}

Widget renderField(FieldNode node, NodeContext ctx) => FieldNodeView(node: node, ctx: ctx);

/// A view field bound to its field neuron (and/or `AggregateScreen` `UiCommand`):
/// - Supports explicit `uiLabel` (including `""` to omit label), `uiValue`
///   (controlled state sync), `uiHelper`, `uiError`, and `uiLive` (`.OnChange`
///   live debounced activation) via `onSubmit.args`.
/// - Humanizes slash neuron keys (`owner/apps/files/name` -> `Name`) so internal
///   storage keys never leak as raw lowercase slugs.
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
  int _sent = 0;

  static const debounce = Duration(milliseconds: 250);

  bool get _secret =>
      widget.node.fieldKind == FieldKind.secret ||
      widget.node.fieldKind == FieldKind.password;

  String get _name => widget.ctx.fieldName(widget.node.name);

  Map<String, dynamic> get _meta => _fieldMeta(widget.node);

  bool get _liveChange => _meta['uiLive'] == true;

  String? get _controlledValue =>
      _meta['uiValue'] is String ? _meta['uiValue'] as String : null;

  @override
  void initState() {
    super.initState();
    if (!_secret && _controlledValue != null) {
      _controller.text = _controlledValue!;
    }
    _read();
    _watch = widget.ctx.watch
        ?.call('field', _name)
        .listen((_) => _read(), onError: (Object _) {});
  }

  @override
  void didUpdateWidget(covariant FieldNodeView oldWidget) {
    super.didUpdateWidget(oldWidget);
    // The control's key is its wire key; a rebinding to another field neuron reads that one.
    if (oldWidget.ctx.fieldName(oldWidget.node.name) !=
        widget.ctx.fieldName(widget.node.name)) {
      _read();
    }
    if (!_secret && !_dirty && !_focus.hasFocus) {
      final nextControlled = _controlledValue;
      if (nextControlled != null && nextControlled != _controller.text) {
        _controller.text = nextControlled;
      }
    }
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
      // Field neuron is optional for pure AggregateScreen commands.
    }
  }

  void _changed(String value) {
    _dirty = true;
    _debounce?.cancel();
    _debounce = Timer(debounce, _post);
  }

  void _post() {
    _debounce?.cancel();
    _debounce = null;
    final number = ++_sent;
    final text = _controller.text;
    unawaited(
      widget.ctx
          .input(widget.node.name, text)
          .whenComplete(() {
            if (number == _sent) _dirty = false;
          })
          .catchError((Object _) {}),
    );
    if (_liveChange && widget.node.onSubmit != null) {
      unawaited(
        widget.ctx
            .activate(widget.node.key, value: text)
            .catchError((Object _) {}),
      );
    }
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
    if (_debounce != null) _post();
    _watch?.cancel();
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final node = widget.node;
    final meta = _meta;
    final label = meta.containsKey('uiLabel')
        ? '${meta['uiLabel'] ?? ''}'
        : _humanizeFieldPath(node.name);
    final helper = meta['uiHelper'] is String
        ? meta['uiHelper'] as String
        : null;
    final error = meta['uiError'] is String ? meta['uiError'] as String : null;
    final clearable =
        node.fieldKind == FieldKind.search ||
        node.fieldKind == FieldKind.text ||
        node.fieldKind == FieldKind.suggest;
    return UiKitField(
      label: label,
      controller: _controller,
      focusNode: _focus,
      placeholder: node.placeholder,
      helper: helper,
      error: error,
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
