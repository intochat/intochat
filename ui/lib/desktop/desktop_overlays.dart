import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

import 'os_bar.dart';

part 'desktop_bar_popovers.dart';

enum DesktopPopoverKind { none, clock, tokens, notifications, dockMenu }

@immutable
class DesktopWindowSummary {
  const DesktopWindowSummary({
    required this.windowId,
    required this.numericId,
    required this.appId,
    required this.title,
    this.minimized = false,
    this.isPinned = true,
  });

  final String windowId;
  final int numericId;
  final String appId;
  final String title;
  final bool minimized;
  final bool isPinned;
}

/// `Alt+\`` (`⌥\``) visual MRU window thumbnail switcher with `IcAppIcon` previews.
class AltBacktickWindowSwitcher extends StatelessWidget {
  const AltBacktickWindowSwitcher({
    super.key,
    required this.windows,
    required this.selectedIndex,
    required this.onSelectWindow,
    required this.onCancel,
  });

  final List<DesktopWindowSummary> windows;
  final int selectedIndex;
  final ValueChanged<DesktopWindowSummary> onSelectWindow;
  final VoidCallback onCancel;

  @override
  Widget build(BuildContext context) {
    if (windows.isEmpty) return const SizedBox.shrink();
    final c = UiColors.of(context);

    return GestureDetector(
      key: const ValueKey('desktop-window-switcher'),
      behavior: HitTestBehavior.opaque,
      onTap: onCancel,
      child: ColoredBox(
        color: Color.fromRGBO(0, 0, 0, c.isDark ? 0.42 : 0.22),
        child: Center(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(UiTokens.of(context).window),
            child: BackdropFilter(
              filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
              child: Container(
                padding: const EdgeInsets.all(14),
                decoration: BoxDecoration(
                  color: c.surfaceElevated.withValues(alpha: 0.94),
                  borderRadius: BorderRadius.circular(
                    UiTokens.of(context).window,
                  ),
                  border: Border.all(
                    color: c.isGlass ? c.specularBorder : c.borderHairline,
                  ),
                  boxShadow: UiShadows.xl(c),
                ),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        for (var i = 0; i < windows.length; i++) ...[
                          if (i > 0) const SizedBox(width: 10),
                          _WindowThumb(
                            window: windows[i],
                            selected: i == selectedIndex,
                            onTap: () => onSelectWindow(windows[i]),
                          ),
                        ],
                      ],
                    ),
                    const SizedBox(height: 10),
                    Text(
                      '⌥`   Release ⌥ to switch  ·  Esc to cancel',
                      style: UiType.textStyle(12, color: c.textMuted),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _WindowThumb extends StatefulWidget {
  const _WindowThumb({
    required this.window,
    required this.selected,
    required this.onTap,
  });

  final DesktopWindowSummary window;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_WindowThumb> createState() => _WindowThumbState();
}

class _WindowThumbState extends State<_WindowThumb> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final sel = widget.selected;
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: Container(
          width: 168,
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: sel
                ? c.accentTint
                : (_hover ? c.surfaceSunk : c.surfaceCard),
            borderRadius: BorderRadius.circular(UiTokens.of(context).card),
            border: Border.all(
              color: sel ? c.accent : c.borderHairline,
              width: sel ? 2 : 1,
            ),
          ),
          child: Column(
            children: [
              Container(
                height: 84,
                decoration: BoxDecoration(
                  color: c.surfacePage,
                  borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
                  border: Border.all(color: c.borderHairline),
                ),
                child: Column(
                  children: [
                    Container(
                      height: 16,
                      padding: const EdgeInsets.symmetric(horizontal: 6),
                      decoration: BoxDecoration(
                        color: c.titleBarFocused,
                        borderRadius: BorderRadius.vertical(
                          top: Radius.circular(UiTokens.of(context).sm - 1),
                        ),
                      ),
                      child: Row(
                        children: [
                          IcAppIcon(appId: widget.window.appId, size: 10),
                          const SizedBox(width: 4),
                          Expanded(
                            child: Text(
                              widget.window.title,
                              overflow: TextOverflow.ellipsis,
                              style: UiType.textStyle(9.5, color: c.textMuted),
                            ),
                          ),
                        ],
                      ),
                    ),
                    Expanded(
                      child: Center(
                        child: IcAppIcon(appId: widget.window.appId, size: 30),
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 8),
              Text(
                widget.window.title,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(
                  12.5,
                  weight: FontWeight.w600,
                  color: c.textStrong,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
