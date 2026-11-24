import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';

/// Mix-styled adaptive sheet container used by [showUiSheet] or standalone.
final class UiSheet extends StatelessWidget {
  const UiSheet({
    super.key,
    required this.child,
    this.title,
    this.subtitle,
    this.trailing,
    this.footer,
    this.showDragHandle = false,
    this.maxWidth = 520,
    this.padding,
    this.semanticsId,
  });

  final Widget child;
  final String? title;
  final String? subtitle;
  final Widget? trailing;
  final Widget? footer;
  final bool showDragHandle;
  final double maxWidth;
  final EdgeInsetsGeometry? padding;
  final String? semanticsId;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final type = UiType.of(context);
    final hasHeader =
        (title != null && title!.isNotEmpty) ||
        (subtitle != null && subtitle!.isNotEmpty) ||
        trailing != null;

    final content = ConstrainedBox(
      constraints: BoxConstraints(maxWidth: maxWidth),
      child: Box(
        style: BoxStyler()
            .color(c.elevated)
            .borderRadius(BorderRadiusGeometryMix.circular(tokens.modalRadius))
            .border(
              BoxBorderMix.all(BorderSideMix.color(c.hairline).width(1.0)),
            )
            .shadow(
              BoxShadowMix(
                color: c.ink.withValues(alpha: 0.16),
                blurRadius: 24,
                offset: const Offset(0, 8),
              ),
            ),
        child: Padding(
          padding: padding ?? EdgeInsets.all(tokens.p20),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (showDragHandle) ...[
                Center(
                  child: Box(
                    style: BoxStyler()
                        .width(36)
                        .height(4)
                        .color(c.borderStrong)
                        .borderRadius(
                          BorderRadiusGeometryMix.circular(tokens.pill),
                        ),
                  ),
                ),
                SizedBox(height: tokens.p12),
              ],
              if (hasHeader) ...[
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          if (title != null && title!.isNotEmpty)
                            Text(
                              title!,
                              style: type.body.copyWith(
                                fontSize: 16,
                                fontWeight: FontWeight.w600,
                                color: c.strong,
                              ),
                            ),
                          if (subtitle != null && subtitle!.isNotEmpty) ...[
                            SizedBox(height: tokens.p4),
                            Text(subtitle!, style: type.meta),
                          ],
                        ],
                      ),
                    ),
                    if (trailing != null) ...[
                      SizedBox(width: tokens.p12),
                      trailing!,
                    ],
                  ],
                ),
                SizedBox(height: tokens.p16),
              ],
              Flexible(child: child),
              if (footer != null) ...[SizedBox(height: tokens.p16), footer!],
            ],
          ),
        ),
      ),
    );

    if (semanticsId == null) return content;
    return Semantics(
      identifier: semanticsId,
      scopesRoute: true,
      explicitChildNodes: true,
      child: content,
    );
  }
}

/// Presents an adaptive modal sheet:
/// - Bottom sheet on compact/mobile viewports (`width < compactBreakpoint`)
/// - Centered dialog (or right-aligned side sheet when [sideSheet] is true) on desktop
Future<T?> showUiSheet<T>({
  required BuildContext context,
  required WidgetBuilder builder,
  bool barrierDismissible = true,
  double compactBreakpoint = 640,
  bool sideSheet = false,
}) {
  final width = MediaQuery.sizeOf(context).width;
  final isCompact = width < compactBreakpoint;

  if (isCompact) {
    return showModalBottomSheet<T>(
      context: context,
      isScrollControlled: true,
      isDismissible: barrierDismissible,
      enableDrag: barrierDismissible,
      backgroundColor: Colors.transparent,
      builder: (ctx) => SafeArea(
        top: false,
        child: Padding(
          padding: EdgeInsets.only(
            left: 8,
            right: 8,
            bottom: MediaQuery.viewInsetsOf(ctx).bottom + 8,
          ),
          child: builder(ctx),
        ),
      ),
    );
  }

  final tokens = UiTokens.of(context);
  return showGeneralDialog<T>(
    context: context,
    barrierDismissible: barrierDismissible,
    barrierLabel: MaterialLocalizations.of(context).modalBarrierDismissLabel,
    barrierColor: Colors.black.withValues(alpha: 0.45),
    transitionDuration: tokens.base,
    pageBuilder: (ctx, _, _) => SafeArea(
      child: Align(
        alignment: sideSheet ? Alignment.centerRight : Alignment.center,
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Material(type: MaterialType.transparency, child: builder(ctx)),
        ),
      ),
    ),
    transitionBuilder: (ctx, animation, _, child) {
      final curved = CurvedAnimation(parent: animation, curve: tokens.ease);
      if (sideSheet) {
        return SlideTransition(
          position: Tween<Offset>(
            begin: const Offset(0.08, 0),
            end: Offset.zero,
          ).animate(curved),
          child: FadeTransition(opacity: curved, child: child),
        );
      }
      return FadeTransition(
        opacity: curved,
        child: ScaleTransition(
          scale: Tween<double>(begin: 0.96, end: 1.0).animate(curved),
          child: child,
        ),
      );
    },
  );
}
