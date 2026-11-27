import 'dart:async';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../primitives/button.dart';
import '../primitives/field.dart';
import '../primitives/icon_button.dart';
import '../primitives/menu.dart';
import '../primitives/style.dart';
import '../theme/ui_icon.dart';
import '../theme/wire_node_values.dart';
import 'node_context.dart';

// --- src/nodes/button.dart ---
const _guardArgKeys = ['guardReason', 'tooltip'];

String? _resolveButtonHint(ButtonNode node) {
  final tooltip = node.tooltip;
  if (tooltip != null && tooltip.trim().isNotEmpty) {
    return tooltip.trim();
  }
  final args = node.command?.args;
  if (args is Map) {
    for (final k in _guardArgKeys) {
      final v = args[k];
      if (v is String && v.trim().isNotEmpty) {
        return v.trim();
      }
    }
  }
  return null;
}

/// A button sends its command through [pressCommand], or performs its
/// client-local intent. A refusal is shown by the host. When guarded
/// (`.Guard(rule)`) or carrying a tooltip, renders a `DESIGN.md` §10.8 [Tooltip].
///
/// Routes both legacy string presets (`"primary"`, `"secondary"`, `"ghost"`,
/// `"danger"`, `"link"`) and structured C# `Mix.Style()` JSON objects through
/// the single [UiMixButton] primitive.
Widget renderButton(ButtonNode node, NodeContext ctx) => !node.visible
    ? const SizedBox.shrink()
    : Builder(
        builder: (context) {
          final interactive = node.enabled && ctx.interactive;
          final onPressed = interactive
              ? () => pressCommand(
                  node.key,
                  node.command,
                  ctx,
                  context,
                  intent: node.intent,
                )
              : null;
          final tooltip = _resolveButtonHint(node);
          final rawStyle = node.style;
          final structuredStyle = rawStyle is Map
              ? UiStyleData.fromWire(rawStyle)
              : null;
          final preset = parseButtonStyle(
            rawStyle is String ? rawStyle : '',
            node.tone,
          );

          final child = UiMixButton(
            key: ValueKey('button:${node.key}'),
            label: node.label,
            icon: node.icon,
            compact: node.small,
            tooltip: tooltip,
            style: structuredStyle,
            preset: preset,
            enabled: interactive,
            onPressed: onPressed,
          );

          return Semantics(
            label: node.automationName.isEmpty ? node.key : node.automationName,
            button: true,
            child: child,
          );
        },
      );

/// Sends a control's [command] through [NodeContext.act] (`POST /act`), the
/// one route of a person's action, with the child-aware target and a field's
/// [value] beside the args (the server adds it as `value` and the screen
/// stamps who acted). A node with no command performs its client-local
/// [intent] itself; the server never sees it. [key] names the node for the
/// host's refusal message only.
Future<ActivationOutcome> pressCommand(
  String key,
  Command? command,
  NodeContext ctx,
  BuildContext? context, {
  String? value,
  IntentNode? intent,
}) async {
  if (command == null || command.target.isEmpty || command.signal.isEmpty) {
    if (intent == null || !ctx.interactive) return ActivationOutcome.refused;
    final performed = await ctx.perform(
      intent,
      context: context != null && context.mounted ? context : null,
    );
    return performed ? ActivationOutcome.accepted : ActivationOutcome.refused;
  }
  return ctx.act(
    command.target,
    command.signal,
    command.args ?? const <String, Object?>{},
    value: value,
    context: context,
  );
}

// --- src/nodes/icon_button.dart ---
Widget renderIconButton(IconButtonNode node, NodeContext ctx) => !node.visible
    ? const SizedBox.shrink()
    : Builder(
        builder: (context) {
          final interactive = node.enabled && ctx.interactive;
          final onPressed = interactive
              ? () => pressCommand(
                  node.key,
                  node.command,
                  ctx,
                  context,
                  intent: node.intent,
                )
              : null;
          final iconData = uiIcon(node.icon) ?? Icons.circle_outlined;
          final structuredStyle = node.style is Map
              ? UiStyleData.fromWire(node.style)
              : null;
          final child = structuredStyle != null
              ? UiMixIconButton(
                  key: ValueKey('iconButton:${node.key}'),
                  icon: iconData,
                  tooltip: node.tooltip,
                  enabled: interactive,
                  style: structuredStyle,
                  onPressed: onPressed,
                )
              : UiGhostIconButton(
                  key: ValueKey('iconButton:${node.key}'),
                  icon: iconData,
                  label: node.tooltip,
                  onPressed: onPressed,
                );
          return Semantics(
            label: node.automationName.isEmpty ? node.key : node.automationName,
            button: true,
            child: child,
          );
        },
      );

// --- src/nodes/field.dart ---
String _humanizeFieldPath(String rawName) => humanizeFieldLabel(rawName);

Map<String, dynamic> _fieldMeta(FieldNode node) {
  final args = node.onSubmit?.args;
  return args is Map ? Map<String, dynamic>.from(args) : const {};
}

Widget renderField(FieldNode node, NodeContext ctx) =>
    FieldNodeView(node: node, ctx: ctx);

/// A view field. What the person types stays in the widget until the field's
/// command sends it as `value` (`onSubmit`, through [pressCommand]); no neuron
/// stores it on the way.
/// - Supports explicit `uiLabel` (including `""` to omit label), `uiValue`
///   (the text the screen's state holds, shown while the person is not typing),
///   `uiHelper`, `uiError`, and `uiLive` (the command is also sent after each
///   pause in typing and when the field loses focus) via `onSubmit.args`.
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
  Timer? _debounce;
  bool _dirty = false;
  int _sent = 0;

  static const debounce = Duration(milliseconds: 250);

  bool get _secret =>
      widget.node.fieldKind == FieldKind.secret ||
      widget.node.fieldKind == FieldKind.password;

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
    _focus.addListener(_focusChanged);
  }

  @override
  void didUpdateWidget(covariant FieldNodeView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (!_secret && !_dirty && !_focus.hasFocus) {
      final nextControlled = _controlledValue;
      if (nextControlled != null && nextControlled != _controller.text) {
        _controller.text = nextControlled;
      }
    }
  }

  // Leaving a live field sends what is still waiting for its pause, so a
  // button pressed right after typing acts on the text the person sees.
  void _focusChanged() {
    if (!_focus.hasFocus && _debounce != null) _post();
  }

  // Typed text is the person's until it is sent: a redraw does not replace it.
  void _changed(String value) {
    _dirty = true;
    if (!_liveChange || widget.node.onSubmit == null) return;
    _debounce?.cancel();
    _debounce = Timer(debounce, _post);
  }

  // One command per pause: the field's command with the typed text as value.
  void _post() {
    _debounce?.cancel();
    _debounce = null;
    final number = ++_sent;
    unawaited(
      pressCommand(
            widget.node.key,
            widget.node.onSubmit,
            widget.ctx,
            mounted ? context : null,
            value: _controller.text,
          )
          .whenComplete(() {
            if (number == _sent) _dirty = false;
          })
          .catchError((Object _) => ActivationOutcome.refused),
    );
  }

  void _submitted(String value) {
    if (widget.node.onSubmit == null) return;
    _debounce?.cancel();
    _debounce = null;
    final number = ++_sent;
    unawaited(
      pressCommand(
            widget.node.key,
            widget.node.onSubmit,
            widget.ctx,
            mounted ? context : null,
            value: value,
          )
          .whenComplete(() {
            if (number == _sent) _dirty = false;
          })
          .catchError((Object _) => ActivationOutcome.refused),
    );
    if (_secret) _controller.clear();
  }

  @override
  void dispose() {
    if (_debounce != null) _post();
    _focus.removeListener(_focusChanged);
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
    return Semantics(
      label: controlNameOf(node.automationName, label, node.key),
      container: true,
      child: UiMixField(
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
      ),
    );
  }
}

// --- src/nodes/intent.dart ---

/// A bare intent is what a button performs; on its own it draws nothing.
Widget renderIntent(IntentNode node, NodeContext ctx) =>
    const SizedBox.shrink();
