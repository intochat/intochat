import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';
import 'package:mix/mix.dart';
import 'package:naked_ui/naked_ui.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_icon.dart';
import '../theme/ui_shadows.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import 'surface.dart';

/// Desktop-dense text field: 13/600 label, 6 gap, then a 36px single-line box
/// with radius 12, `inputBorder` border, `surface` fill and xs shadow. Focus
/// turns the border `accent` and adds a 3px `focus` ring outside.
typedef UiKitField = UiMixField;

final class UiMixField extends StatefulWidget {
  const UiMixField({
    super.key,
    required this.label,
    this.controller,
    this.placeholder,
    this.error,
    this.secret = false,
    this.clearable = false,
    this.showSecretToggle = true,
    this.leadingIcon,
    this.keyboardType,
    this.onSubmitted,
    this.onChanged,
    this.focusNode,
    this.enabled = true,
    this.multiline = false,
    this.helper,
    this.textInputAction,
    this.fieldKey,
    this.autofillHints,
    this.autofocus = false,
    this.autocorrect = true,
    this.suffix,
  });

  final String label;
  final TextEditingController? controller;
  final String? placeholder;
  final String? error;
  final bool secret;

  /// When true (and not [multiline] or [secret]), shows an inline `(x)` clear
  /// button whenever the field contains text and [enabled] is true (`UK-12`).
  final bool clearable;

  /// When [secret] is true and no custom [suffix] is provided, renders an
  /// accessible show/hide (`eye` / `eye-off`) toggle button inside the field.
  final bool showSecretToggle;

  /// Optional leading icon name (resolved via [uiIcon]), e.g. `'search'`.
  final String? leadingIcon;

  final TextInputType? keyboardType;
  final ValueChanged<String>? onSubmitted;
  final ValueChanged<String>? onChanged;
  final FocusNode? focusNode;
  final bool enabled;
  final bool multiline;
  final String? helper;
  final TextInputAction? textInputAction;

  /// Key for the inner [TextField], for callers that address it directly.
  final Key? fieldKey;

  final Iterable<String>? autofillHints;
  final bool autofocus;

  /// When false, also turns off suggestions (usernames, passwords).
  final bool autocorrect;

  /// Trailing widget inside the box, e.g. a reveal-password icon button.
  final Widget? suffix;

  @override
  State<UiMixField> createState() => _UiMixFieldState();
}

class _UiMixFieldState extends State<UiMixField> {
  FocusNode? _ownFocus;
  TextEditingController? _ownController;
  bool _hover = false;
  late bool _obscured = widget.secret;

  FocusNode get _focus => widget.focusNode ?? (_ownFocus ??= FocusNode());
  TextEditingController get _effectiveController =>
      widget.controller ?? (_ownController ??= TextEditingController());

  @override
  void initState() {
    super.initState();
    _focus.addListener(_refresh);
    _effectiveController.addListener(_refresh);
  }

  @override
  void didUpdateWidget(UiMixField old) {
    super.didUpdateWidget(old);
    if (old.secret != widget.secret) {
      _obscured = widget.secret;
    }
    final previousFocus = old.focusNode ?? _ownFocus;
    if (previousFocus != _focus) {
      previousFocus?.removeListener(_refresh);
      _focus.addListener(_refresh);
    }
    if (old.controller != widget.controller) {
      old.controller?.removeListener(_refresh);
      _ownController?.removeListener(_refresh);
      _effectiveController.addListener(_refresh);
    }
  }

  void _refresh() {
    if (mounted) setState(() {});
  }

  void _clearText() {
    if (!widget.enabled) return;
    _effectiveController.clear();
    widget.onChanged?.call('');
    _focus.requestFocus();
  }

  @override
  void dispose() {
    _focus.removeListener(_refresh);
    _effectiveController.removeListener(_refresh);
    _ownFocus?.dispose();
    _ownController?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    final tokens = UiTokens.of(context);
    final radius = tokens.fieldRadius;
    final focused = _focus.hasFocus;
    final error = widget.error;
    final hasError = error != null && error.isNotEmpty;
    final borderColor = hasError
        ? c.error
        : focused
        ? c.accent
        : _hover && widget.enabled
        ? c.muted
        : c.inputBorder;
    final placeholder = widget.placeholder;
    final style = type.body.copyWith(color: c.strong);
    final hintStyle = type.body.copyWith(color: c.subtle);
    final name = widget.label.isNotEmpty ? widget.label : placeholder;
    final hasText = _effectiveController.text.isNotEmpty;
    final canClear =
        widget.clearable &&
        !widget.secret &&
        !widget.multiline &&
        widget.enabled &&
        hasText;

    final Widget? resolvedSuffix =
        widget.suffix ??
        (widget.secret && widget.showSecretToggle
            ? _FieldIconAction(
                key: const ValueKey('ui_kit_field_secret_toggle'),
                tooltip: _obscured ? 'Show secret' : 'Hide secret',
                icon: _obscured ? LucideIcons.eye : LucideIcons.eyeOff,
                color: c.muted,
                enabled: widget.enabled,
                onTap: () => setState(() => _obscured = !_obscured),
              )
            : canClear
            ? _FieldIconAction(
                key: const ValueKey('ui_kit_field_clear'),
                tooltip: 'Clear',
                icon: LucideIcons.x,
                color: c.muted,
                enabled: widget.enabled,
                onTap: _clearText,
              )
            : null);

    final leadingGlyph =
        widget.leadingIcon != null && widget.leadingIcon!.isNotEmpty
        ? uiIcon(widget.leadingIcon!)
        : null;

    Widget textField = TextField(
      key: widget.fieldKey,
      controller: _effectiveController,
      focusNode: _focus,
      enabled: widget.enabled,
      obscureText: widget.secret && _obscured,
      keyboardType: widget.keyboardType,
      minLines: widget.multiline ? 2 : 1,
      maxLines: widget.multiline ? 5 : 1,
      textInputAction: widget.textInputAction,
      autofillHints: widget.autofillHints,
      autofocus: widget.autofocus,
      autocorrect: widget.autocorrect,
      enableSuggestions: widget.autocorrect,
      style: style,
      cursorColor: c.accent,
      decoration:
          InputDecoration.collapsed(
            hintText: null,
            hintStyle: hintStyle,
          ).copyWith(
            hint: placeholder == null
                ? null
                : ExcludeSemantics(
                    child: Text(
                      placeholder,
                      style: Theme.of(context).textTheme.bodyLarge!
                          .merge(style)
                          .merge(hintStyle),
                    ),
                  ),
            enabledBorder: InputBorder.none,
            focusedBorder: InputBorder.none,
            disabledBorder: InputBorder.none,
            errorBorder: InputBorder.none,
            focusedErrorBorder: InputBorder.none,
            filled: false,
          ),
      onChanged: widget.onChanged,
      onSubmitted: widget.onSubmitted,
    );

    if (canClear) {
      textField = CallbackShortcuts(
        bindings: {
          const SingleActivator(LogicalKeyboardKey.escape): _clearText,
        },
        child: textField,
      );
    }

    final input = MergeSemantics(
      child: Semantics(label: name, child: textField),
    );
    final shadows = UiShadows.xs(uiKitShadowBase(context));
    var boxStyle = BoxStyler()
        .color(c.surface)
        .borderRadius(BorderRadiusGeometryMix.circular(radius))
        .border(BoxBorderMix.all(BorderSideMix.color(borderColor).width(1.0)))
        .padding(
          EdgeInsetsGeometryMix.fromLTRB(
            leadingGlyph == null ? 12 : 10,
            widget.multiline ? 6 : 0,
            resolvedSuffix == null ? 12 : 4,
            widget.multiline ? 6 : 0,
          ),
        )
        .animate(
          AnimationConfig.curve(duration: tokens.fast, curve: tokens.ease),
        );
    for (final s in shadows) {
      boxStyle = boxStyle.shadow(
        BoxShadowMix(
          color: s.color,
          blurRadius: s.blurRadius,
          spreadRadius: s.spreadRadius,
          offset: s.offset,
        ),
      );
    }

    final box = Box(
      key: const ValueKey('ui_kit_field_box'),
      style: boxStyle,
      child: widget.multiline
          ? ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 48),
              child: Align(alignment: Alignment.centerLeft, child: input),
            )
          : SizedBox(
              // The 1 px border sits outside this box, so the field stays
              // inputHeight tall overall.
              height: tokens.inputHeight - 2,
              child: leadingGlyph == null && resolvedSuffix == null
                  ? Align(alignment: Alignment.centerLeft, child: input)
                  : Row(
                      children: [
                        if (leadingGlyph != null) ...[
                          Icon(leadingGlyph, size: 15, color: c.muted),
                          const SizedBox(width: 8),
                        ],
                        Expanded(child: input),
                        ?resolvedSuffix,
                      ],
                    ),
            ),
    );
    final helper = widget.helper;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (widget.label.isNotEmpty) ...[
          ExcludeSemantics(child: Text(widget.label, style: type.fieldLabel)),
          const SizedBox(height: 6),
        ],
        MouseRegion(
          onEnter: (_) => setState(() => _hover = true),
          onExit: (_) => setState(() => _hover = false),
          child: Stack(
            clipBehavior: Clip.none,
            children: [
              if (focused)
                Positioned(
                  left: -3,
                  top: -3,
                  right: -3,
                  bottom: -3,
                  child: IgnorePointer(
                    child: Box(
                      key: const ValueKey('ui_kit_field_ring'),
                      style: BoxStyler()
                          .borderRadius(
                            BorderRadiusGeometryMix.circular(radius + 3),
                          )
                          .border(
                            BoxBorderMix.all(
                              BorderSideMix.color(c.focus).width(3.0),
                            ),
                          ),
                    ),
                  ),
                ),
              box,
            ],
          ),
        ),
        if (hasError) ...[
          const SizedBox(height: 6),
          Text(error, style: type.meta.copyWith(color: c.error)),
        ] else if (helper != null) ...[
          const SizedBox(height: 6),
          Text(helper, style: type.meta),
        ],
      ],
    );
  }
}

class _FieldIconAction extends StatelessWidget {
  const _FieldIconAction({
    super.key,
    required this.tooltip,
    required this.icon,
    required this.color,
    required this.enabled,
    required this.onTap,
  });

  final String tooltip;
  final IconData icon;
  final Color color;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: tooltip,
      child: NakedButton(
        enabled: enabled,
        semanticLabel: tooltip,
        onPressed: enabled ? onTap : null,
        child: Box(
          style: BoxStyler().width(28).height(28),
          child: Center(child: Icon(icon, size: 15, color: color)),
        ),
      ),
    );
  }
}
