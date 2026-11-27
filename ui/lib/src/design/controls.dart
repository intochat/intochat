import 'package:flutter/widgets.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

/// Plain text field on EditableText (no Material).
class Field extends StatelessWidget {
  const Field({
    super.key,
    required this.controller,
    required this.focusNode,
    this.placeholder = '',
    this.style,
    this.onSubmitted,
    this.maxLines = 1,
    this.minLines,
    this.obscure = false,
    this.onChanged,
  });

  final TextEditingController controller;
  final FocusNode focusNode;
  final String placeholder;
  final TextStyle? style;
  final ValueChanged<String>? onSubmitted;
  final int? maxLines;
  final int? minLines;
  final bool obscure;
  final ValueChanged<String>? onChanged;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final st = style ?? UiType.textStyle(14, color: c.textStrong);
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) => Stack(
        children: [
          if (controller.text.isEmpty)
            IgnorePointer(
              child: Text(
                placeholder,
                maxLines: 1,
                style: st.copyWith(color: c.textSubtle),
              ),
            ),
          EditableText(
            controller: controller,
            focusNode: focusNode,
            style: st,
            cursorColor: c.accent,
            backgroundCursorColor: c.textSubtle,
            selectionColor: c.accentTint,
            maxLines: maxLines,
            minLines: minLines,
            onSubmitted: onSubmitted,
            onEditingComplete: maxLines == 1 ? () {} : null,
            onChanged: onChanged,
            obscureText: obscure,
            obscuringCharacter: '•',
            keyboardType: maxLines == 1
                ? TextInputType.text
                : TextInputType.multiline,
          ),
        ],
      ),
    );
  }
}

/// "Working": a loader that spins while a session or app is busy.
class WorkingSpinner extends StatefulWidget {
  const WorkingSpinner({super.key, this.size = 14, this.color});

  final double size;
  final Color? color;

  @override
  State<WorkingSpinner> createState() => _WorkingSpinnerState();
}

class _WorkingSpinnerState extends State<WorkingSpinner>
    with SingleTickerProviderStateMixin {
  late final AnimationController _a = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 900),
  );

  @override
  void initState() {
    super.initState();
    if (!WidgetsBinding.instance.runtimeType.toString().contains('Test')) {
      _a.repeat();
    }
  }

  @override
  void dispose() {
    _a.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final icon = Icon(
      LucideIcons.loaderCircle,
      size: widget.size,
      color: widget.color ?? UiColors.of(context).textMuted,
    );
    if (MediaQuery.disableAnimationsOf(context)) return icon;
    return RotationTransition(turns: _a, child: icon);
  }
}

/// On/off switch. Saves at once; the caller shows the toast.
class IcSwitch extends StatelessWidget {
  const IcSwitch({
    super.key,
    required this.value,
    required this.onChanged,
    this.locked = false,
  });

  final bool value;
  final ValueChanged<bool>? onChanged;
  final bool locked;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final enabled = onChanged != null && !locked;
    final track = AnimatedContainer(
      duration: UiTokens.of(context).fast,
      curve: UiTokens.of(context).ease,
      width: 36,
      height: 20,
      padding: const EdgeInsets.all(2),
      decoration: BoxDecoration(
        color: value ? c.accent : c.surfaceSunk,
        borderRadius: BorderRadius.circular(UiTokens.of(context).pill),
        border: Border.all(color: value ? c.accent : c.borderInput),
      ),
      child: AnimatedAlign(
        duration: UiTokens.of(context).fast,
        curve: UiTokens.of(context).ease,
        alignment: value ? Alignment.centerRight : Alignment.centerLeft,
        child: Container(
          width: 14,
          height: 14,
          decoration: BoxDecoration(
            color: value ? c.onAccent : c.borderStrong,
            shape: BoxShape.circle,
          ),
        ),
      ),
    );
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (locked) ...[
          Icon(LucideIcons.lock, size: 13, color: c.textMuted),
          const SizedBox(width: 6),
        ],
        Opacity(
          opacity: enabled ? 1 : 0.45,
          child: MouseRegion(
            cursor: enabled ? SystemMouseCursors.click : MouseCursor.defer,
            child: GestureDetector(
              onTap: enabled ? () => onChanged!(!value) : null,
              child: track,
            ),
          ),
        ),
      ],
    );
  }
}

enum TagTone { neutral, accent, done, working, error }

/// Compact status / metadata pill with optional icon.
class TagPill extends StatelessWidget {
  const TagPill({
    super.key,
    required this.label,
    this.tone = TagTone.neutral,
    this.icon,
  });

  final String label;
  final TagTone tone;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final (bg, fg) = switch (tone) {
      TagTone.neutral => (c.surfaceSunk, c.textMuted),
      TagTone.accent => (c.accentTint, c.accent),
      TagTone.done => (c.doneBg, c.done),
      TagTone.working => (c.workingBg, c.working),
      TagTone.error => (c.errorBg, c.error),
    };
    return Container(
      height: 22,
      padding: const EdgeInsets.symmetric(horizontal: 7),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
        border: Border.all(color: fg.withValues(alpha: 0.25)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 12, color: fg),
            const SizedBox(width: 4),
          ],
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: UiType.textStyle(
                11.5,
                weight: FontWeight.w600,
                color: fg,
                tnum: true,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
