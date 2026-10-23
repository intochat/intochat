import 'package:flutter/material.dart';

import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';

/// Badge value node: pill, 4x7 padding, mono 11. Tone 1 Done, 2 Working,
/// 3 Error, otherwise Neutral (recessed fill, muted text).
final class ViewBadge extends StatelessWidget {
  const ViewBadge({super.key, required this.text, required this.tone});
  final String text;
  final Object? tone;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final (fill, ink) = switch (tone) {
      1 => (c.doneBg, c.strong),
      2 => (c.workingBg, c.strong),
      3 => (c.errorBg, c.strong),
      _ => (c.recessed, c.muted),
    };
    return Align(
      alignment: AlignmentDirectional.centerStart,
      widthFactor: 1,
      heightFactor: 1,
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: fill,
          borderRadius: BorderRadius.circular(UiTokens.of(context).pill),
        ),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 4),
          child: Text(
            text,
            style: UiType.of(context).meta
                .copyWith(fontSize: 11, color: ink, height: 1),
          ),
        ),
      ),
    );
  }
}
