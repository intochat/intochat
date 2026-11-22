import 'package:flutter/material.dart';
import 'package:mix/mix.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_tokens.dart';

/// Mix-styled animated pulse loading placeholder.
final class UiSkeleton extends StatefulWidget {
  const UiSkeleton({
    super.key,
    this.width,
    this.height = 14,
    this.radius,
    this.circle = false,
    this.semanticsLabel,
  });

  final double? width;
  final double height;
  final double? radius;
  final bool circle;
  final String? semanticsLabel;

  @override
  State<UiSkeleton> createState() => _UiSkeletonState();
}

class _UiSkeletonState extends State<UiSkeleton>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller;

  @override
  void initState() {
    super.initState();
    _controller = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 1100),
    );
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final tokens = UiTokens.of(context);
    if (_controller.duration != tokens.skeleton) {
      _controller.duration = tokens.skeleton;
    }
    if (MediaQuery.disableAnimationsOf(context)) {
      _controller.stop();
      _controller.value = 0.5;
    } else if (!_controller.isAnimating) {
      _controller.repeat(reverse: true);
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final effectiveRadius = widget.circle
        ? tokens.pill
        : (widget.radius ?? tokens.radiusSmall);
    final effectiveWidth = widget.circle
        ? (widget.width ?? widget.height)
        : widget.width;

    return Semantics(
      label: widget.semanticsLabel,
      excludeSemantics: widget.semanticsLabel == null,
      child: AnimatedBuilder(
        animation: _controller,
        builder: (context, _) {
          final color = Color.lerp(
            c.recessed,
            c.hairline.withValues(alpha: 0.7),
            _controller.value,
          )!;
          var style = BoxStyler()
              .height(widget.height)
              .color(color)
              .borderRadius(BorderRadiusGeometryMix.circular(effectiveRadius));
          if (effectiveWidth != null) {
            style = style.width(effectiveWidth);
          }
          return Box(style: style);
        },
      ),
    );
  }
}
