part of 'desktop_overlays.dart';

/// Unclipped hover tooltip rendered above the `36px` `OsBar`.
class BarTooltip extends StatelessWidget {
  const BarTooltip({
    super.key,
    required this.screen,
    required this.barLayout,
    this.label,
    this.anchorX = 0,
  });

  final Size screen;
  final BarLayout barLayout;
  final String? label;
  final double anchorX;

  @override
  Widget build(BuildContext context) {
    final text = label;
    if (text == null || text.isEmpty) return const SizedBox.shrink();
    final c = UiColors.of(context);
    final bottomOffset =
        kOsBarHeight + (barLayout == BarLayout.classicBottom ? 8.0 : 14.0);
    return Stack(
      children: [
        Positioned(
          left: (anchorX - 80).clamp(8.0, math.max(8.0, screen.width - 180.0)),
          bottom: bottomOffset,
          child: IgnorePointer(
            child: Container(
              key: const ValueKey('os-bar-tooltip'),
              height: 24,
              padding: const EdgeInsets.symmetric(horizontal: 8),
              alignment: Alignment.center,
              decoration: BoxDecoration(
                color: c.surfaceElevated,
                borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
                border: Border.all(color: c.borderHairline),
                boxShadow: UiShadows.sm(c),
              ),
              child: Text(
                text,
                style: UiType.textStyle(
                  12,
                  weight: FontWeight.w500,
                  color: c.textStrong,
                ),
              ),
            ),
          ),
        ),
      ],
    );
  }
}

@immutable
class DesktopNotificationEntry {
  const DesktopNotificationEntry({
    required this.id,
    required this.sourceApp,
    required this.title,
    required this.body,
    required this.priority,
    required this.read,
    required this.createdUtc,
    this.actionUrl,
  });

  final String id;
  final String sourceApp;
  final String title;
  final String body;
  final int priority;
  final bool read;
  final DateTime createdUtc;
  final String? actionUrl;
}

/// Acrylic flyout popover for Clock, Tokens & Compute, Notifications, and
/// Right-Click Dock Context Menu.
class DesktopBarPopover extends StatelessWidget {
  const DesktopBarPopover({
    super.key,
    required this.kind,
    required this.screen,
    required this.barLayout,
    this.anchorX = 0,
    this.dockAppId,
    this.dockAppTitle,
    this.tokensToday = 14800,
    this.notificationsCount = 0,
    this.notifications = const [],
    this.notificationsLoaded = false,
    this.notificationsLoading = false,
    this.pinnedAppIds = const {},
    this.windows = const [],
    required this.onClose,
    this.onOpenSettings,
    this.onOpenComputeSettings,
    this.onClearNotifications,
    this.onMarkNotificationRead,
    this.onMarkAllNotificationsRead,
    this.onFocusWindow,
    this.onNewWindowForApp,
    this.onTogglePinApp,
    this.onQuitApp,
  });

  final DesktopPopoverKind kind;
  final Size screen;
  final BarLayout barLayout;
  final double anchorX;
  final String? dockAppId;
  final String? dockAppTitle;
  final int tokensToday;
  final int notificationsCount;
  final List<DesktopNotificationEntry> notifications;
  final bool notificationsLoaded;
  final bool notificationsLoading;
  final Set<String> pinnedAppIds;
  final List<DesktopWindowSummary> windows;
  final VoidCallback onClose;
  final VoidCallback? onOpenSettings;
  final VoidCallback? onOpenComputeSettings;
  final VoidCallback? onClearNotifications;
  final ValueChanged<String>? onMarkNotificationRead;
  final VoidCallback? onMarkAllNotificationsRead;
  final ValueChanged<String>? onFocusWindow;
  final ValueChanged<String>? onNewWindowForApp;
  final ValueChanged<String>? onTogglePinApp;
  final ValueChanged<String>? onQuitApp;

  @override
  Widget build(BuildContext context) {
    if (kind == DesktopPopoverKind.none) return const SizedBox.shrink();
    final c = UiColors.of(context);

    final (double width, Widget content, Key popoverKey) = switch (kind) {
      DesktopPopoverKind.clock => (
        268.0,
        const _ClockPopover(),
        const ValueKey('desktop-popover-clock'),
      ),
      DesktopPopoverKind.tokens => (
        340.0,
        _TokensPopover(
          tokensToday: tokensToday,
          onOpenCompute: () {
            onClose();
            if (onOpenComputeSettings != null) {
              onOpenComputeSettings!();
            } else {
              onOpenSettings?.call();
            }
          },
        ),
        const ValueKey('desktop-popover-tokens'),
      ),
      DesktopPopoverKind.notifications => (
        360.0,
        _NotificationsPopover(
          notificationsCount: notificationsCount,
          notifications: notifications,
          loaded: notificationsLoaded,
          loading: notificationsLoading,
          onTapNotification: (item) {
            if (!item.read) {
              onMarkNotificationRead?.call(item.id);
            }
            onClose();
            final targetApp = _extractAppId(item.sourceApp) ?? 'ino';
            for (final w in windows) {
              if (w.appId == targetApp) {
                onFocusWindow?.call(w.windowId);
                return;
              }
            }
            onNewWindowForApp?.call(targetApp);
          },
          onOpenSettings: () {
            onClose();
            onOpenSettings?.call();
          },
          onMarkAllRead: () {
            (onMarkAllNotificationsRead ?? onClearNotifications)?.call();
            onClose();
          },
          onClearAll: onClearNotifications,
        ),
        const ValueKey('desktop-popover-notifications'),
      ),
      DesktopPopoverKind.dockMenu => (
        228.0,
        _DockContextMenu(
          appId: dockAppId ?? 'ino',
          appTitle: dockAppTitle,
          isPinned: pinnedAppIds.contains(dockAppId ?? 'ino'),
          appWindows: [
            for (final w in windows)
              if (w.appId == (dockAppId ?? 'ino')) w,
          ],
          onFocusWindow: (winId) {
            onClose();
            onFocusWindow?.call(winId);
          },
          onNewWindow: () {
            final target = dockAppId ?? 'ino';
            onClose();
            onNewWindowForApp?.call(target);
          },
          onTogglePin: () {
            final target = dockAppId ?? 'ino';
            onTogglePinApp?.call(target);
            onClose();
          },
          onOpenSettings: () {
            onClose();
            onOpenSettings?.call();
          },
          onQuit: () {
            final target = dockAppId ?? 'ino';
            onClose();
            onQuitApp?.call(target);
          },
        ),
        const ValueKey('desktop-popover-dock-menu'),
      ),
      DesktopPopoverKind.none => (
        0.0,
        const SizedBox.shrink(),
        const ValueKey('none'),
      ),
    };

    final double left = kind == DesktopPopoverKind.dockMenu
        ? (anchorX - width / 2).clamp(
            8.0,
            math.max(8.0, screen.width - width - 8.0),
          )
        : math.max(8.0, screen.width - width - 12.0);
    final bottomOffset =
        kOsBarHeight + (barLayout == BarLayout.classicBottom ? 8.0 : 14.0);

    return Positioned(
      left: left,
      bottom: bottomOffset,
      width: width,
      child: ClipRRect(
        key: popoverKey,
        borderRadius: BorderRadius.circular(UiTokens.of(context).card),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
          child: Container(
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: c.surfaceElevated.withValues(alpha: 0.94),
              borderRadius: BorderRadius.circular(UiTokens.of(context).card),
              border: Border.all(
                color: c.isGlass ? c.specularBorder : c.borderHairline,
              ),
              boxShadow: UiShadows.lg(c),
            ),
            child: content,
          ),
        ),
      ),
    );
  }
}

class _ClockPopover extends StatelessWidget {
  const _ClockPopover();

  static const _weekdays = [
    'Monday',
    'Tuesday',
    'Wednesday',
    'Thursday',
    'Friday',
    'Saturday',
    'Sunday',
  ];

  static const _months = [
    'January',
    'February',
    'March',
    'April',
    'May',
    'June',
    'July',
    'August',
    'September',
    'October',
    'November',
    'December',
  ];

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final now = DateTime.now();
    final headerText =
        '${_weekdays[now.weekday - 1]}, ${now.day} ${_months[now.month - 1]}';
    final firstOfMonth = DateTime(now.year, now.month, 1);
    final startOffset = firstOfMonth.weekday - 1; // 0 = Mon .. 6 = Sun
    final daysInMonth = DateUtils.getDaysInMonth(now.year, now.month);
    final cells = <int?>[
      for (var i = 0; i < startOffset; i++) null,
      for (var d = 1; d <= daysInMonth; d++) d,
    ];
    while (cells.length % 7 != 0) {
      cells.add(null);
    }

    return Padding(
      padding: const EdgeInsets.all(6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            headerText,
            style: UiType.textStyle(
              14,
              weight: FontWeight.w600,
              color: c.textStrong,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            '${_months[now.month - 1]} ${now.year} · DigitalBrain Schedule',
            style: UiType.textStyle(12, color: c.textMuted),
          ),
          const SizedBox(height: 10),
          Row(
            children: [
              for (final d in const ['M', 'T', 'W', 'T', 'F', 'S', 'S'])
                Expanded(
                  child: Center(
                    child: Text(
                      d,
                      style: UiType.textStyle(
                        11,
                        weight: FontWeight.w600,
                        color: c.textSubtle,
                      ),
                    ),
                  ),
                ),
            ],
          ),
          const SizedBox(height: 4),
          for (var r = 0; r < cells.length ~/ 7; r++)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 2),
              child: Row(
                children: [
                  for (var col = 0; col < 7; col++)
                    Expanded(
                      child: Builder(
                        builder: (_) {
                          final day = cells[r * 7 + col];
                          if (day == null) return const SizedBox(height: 26);
                          final today = day == now.day;
                          return Container(
                            height: 26,
                            alignment: Alignment.center,
                            decoration: BoxDecoration(
                              color: today ? c.accent : const Color(0x00000000),
                              borderRadius: BorderRadius.circular(
                                UiTokens.of(context).xs,
                              ),
                            ),
                            child: Text(
                              '$day',
                              style: UiType.textStyle(
                                12,
                                weight: today
                                    ? FontWeight.w600
                                    : FontWeight.w400,
                                color: today ? c.onAccent : c.textBody,
                                tnum: true,
                              ),
                            ),
                          );
                        },
                      ),
                    ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

class _TokensPopover extends StatelessWidget {
  const _TokensPopover({
    required this.tokensToday,
    required this.onOpenCompute,
  });

  final int tokensToday;
  final VoidCallback onOpenCompute;

  static String _fmt(int n) {
    final s = n.toString();
    final buf = StringBuffer();
    for (var i = 0; i < s.length; i++) {
      if (i > 0 && (s.length - i) % 3 == 0) buf.write(',');
      buf.write(s[i]);
    }
    return buf.toString();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    const sessions = [
      ('Ino Assistant', '6,420'),
      ('Workspace Canvas', '4,110'),
      ('ArtCraft Studio', '2,890'),
      ('Background tasks', '1,380'),
    ];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(8, 4, 8, 6),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  'Tokens today',
                  overflow: TextOverflow.ellipsis,
                  style: UiType.textStyle(
                    12,
                    weight: FontWeight.w600,
                    color: c.textMuted,
                  ),
                ),
              ),
              Text(
                _fmt(tokensToday),
                style: UiType.textStyle(
                  15,
                  weight: FontWeight.w600,
                  color: c.textStrong,
                  tnum: true,
                ),
              ),
            ],
          ),
        ),
        Padding(
          padding: const EdgeInsets.fromLTRB(8, 0, 8, 8),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  'Estimated cost',
                  overflow: TextOverflow.ellipsis,
                  style: UiType.textStyle(12, color: c.textMuted),
                ),
              ),
              Text(
                '~\$0.18 · 412k/mo',
                style: UiType.textStyle(12, color: c.textMuted, tnum: true),
              ),
            ],
          ),
        ),
        const _PopoverDivider(),
        for (final s in sessions)
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 5),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    s.$1,
                    overflow: TextOverflow.ellipsis,
                    style: UiType.textStyle(12.5, color: c.textBody),
                  ),
                ),
                Text(
                  s.$2,
                  style: UiType.textStyle(12, color: c.textMuted, tnum: true),
                ),
              ],
            ),
          ),
        const _PopoverDivider(),
        _PopoverItem(
          key: const ValueKey('popover-tokens-open-compute'),
          icon: LucideIcons.coins,
          label: 'Credits and usage',
          trailing: 'Compute',
          onTap: onOpenCompute,
        ),
      ],
    );
  }
}

String? _extractAppId(String sourceApp) {
  final idx = sourceApp.indexOf('/apps/');
  if (idx >= 0) {
    final rest = sourceApp.substring(idx + 6);
    if (rest.isNotEmpty) return rest;
  }
  return switch (sourceApp) {
    'os' || 'user' || 'scheduler' || '' => null,
    'assistant' => 'ino',
    _ => sourceApp,
  };
}

class _NotificationsPopover extends StatelessWidget {
  const _NotificationsPopover({
    required this.notificationsCount,
    required this.notifications,
    required this.loaded,
    required this.loading,
    required this.onTapNotification,
    required this.onOpenSettings,
    required this.onMarkAllRead,
    this.onClearAll,
  });

  final int notificationsCount;
  final List<DesktopNotificationEntry> notifications;
  final bool loaded;
  final bool loading;
  final ValueChanged<DesktopNotificationEntry> onTapNotification;
  final VoidCallback onOpenSettings;
  final VoidCallback onMarkAllRead;
  final VoidCallback? onClearAll;

  static String _formatAge(DateTime createdUtc) {
    final diff = DateTime.now().toUtc().difference(createdUtc.toUtc());
    if (diff.inSeconds < 60) return 'now';
    if (diff.inMinutes < 60) return '${diff.inMinutes}m';
    if (diff.inHours < 24) return '${diff.inHours}h';
    return '${diff.inDays}d';
  }

  static (IconData, Color) _priorityIcon(int priority, UiColors c) =>
      switch (priority) {
        3 => (LucideIcons.triangleAlert, c.error),
        2 => (LucideIcons.hand, c.working),
        0 => (LucideIcons.bell, c.textMuted),
        _ => (LucideIcons.sparkles, c.accent),
      };

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final hasUnread =
        notificationsCount > 0 || notifications.any((n) => !n.read);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        _buildHeader(
          c,
          hasUnread: hasUnread,
          hasItems: loaded && notifications.isNotEmpty,
        ),
        if (loading && notifications.isEmpty)
          Padding(
            key: const ValueKey('popover-notifications-loading'),
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 18),
            child: Center(
              child: Text(
                'Loading notifications…',
                style: UiType.textStyle(12.5, color: c.textMuted),
              ),
            ),
          )
        else if (!loaded || notifications.isEmpty)
          Padding(
            key: const ValueKey('popover-notifications-empty'),
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 20),
            child: Center(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(LucideIcons.bellOff, size: 20, color: c.textSubtle),
                  const SizedBox(height: 6),
                  Text(
                    'No notifications yet',
                    style: UiType.textStyle(12.5, color: c.textMuted),
                  ),
                ],
              ),
            ),
          )
        else
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 300),
            child: SingleChildScrollView(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  for (final it in notifications)
                    _PopoverRow(
                      key: ValueKey('popover-notification-${it.id}'),
                      onTap: () => onTapNotification(it),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(vertical: 4),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Padding(
                              padding: const EdgeInsets.only(top: 2),
                              child: Icon(
                                _priorityIcon(it.priority, c).$1,
                                size: 15,
                                color: _priorityIcon(it.priority, c).$2,
                              ),
                            ),
                            const SizedBox(width: 10),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(
                                    it.title,
                                    style: UiType.textStyle(
                                      13,
                                      weight: it.read
                                          ? FontWeight.w500
                                          : FontWeight.w600,
                                      color: it.read
                                          ? c.textBody
                                          : c.textStrong,
                                    ),
                                  ),
                                  if (it.body.isNotEmpty) ...[
                                    const SizedBox(height: 1),
                                    Text(
                                      it.body,
                                      style: UiType.textStyle(
                                        12,
                                        color: c.textMuted,
                                      ),
                                    ),
                                  ],
                                ],
                              ),
                            ),
                            const SizedBox(width: 8),
                            Column(
                              crossAxisAlignment: CrossAxisAlignment.end,
                              children: [
                                Text(
                                  _formatAge(it.createdUtc),
                                  style: UiType.textStyle(
                                    11.5,
                                    color: c.textSubtle,
                                    tnum: true,
                                  ),
                                ),
                                if (!it.read) ...[
                                  const SizedBox(height: 4),
                                  Container(
                                    width: 6,
                                    height: 6,
                                    decoration: BoxDecoration(
                                      color: c.accent,
                                      shape: BoxShape.circle,
                                    ),
                                  ),
                                ],
                              ],
                            ),
                          ],
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ),
        const _PopoverDivider(),
        _PopoverItem(
          key: const ValueKey('popover-notifications-settings'),
          icon: LucideIcons.bell,
          label: 'Notification settings',
          onTap: onOpenSettings,
        ),
      ],
    );
  }

  Widget _buildHeader(
    UiColors c, {
    required bool hasUnread,
    required bool hasItems,
  }) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 4, 8, 6),
      child: Row(
        children: [
          Expanded(
            child: Text(
              'Notifications',
              overflow: TextOverflow.ellipsis,
              style: UiType.textStyle(
                12,
                weight: FontWeight.w600,
                color: c.textMuted,
              ),
            ),
          ),
          if (hasUnread)
            GestureDetector(
              key: const ValueKey('popover-notifications-mark-read'),
              onTap: onMarkAllRead,
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Text(
                  'Mark all as read',
                  style: UiType.textStyle(
                    12,
                    weight: FontWeight.w500,
                    color: c.accent,
                  ),
                ),
              ),
            ),
          if (hasItems && onClearAll != null) ...[
            if (hasUnread) const SizedBox(width: 10),
            GestureDetector(
              key: const ValueKey('popover-notifications-clear'),
              onTap: onClearAll,
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Text(
                  'Clear all',
                  style: UiType.textStyle(
                    12,
                    weight: FontWeight.w500,
                    color: c.textMuted,
                  ),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _DockContextMenu extends StatelessWidget {
  const _DockContextMenu({
    required this.appId,
    this.appTitle,
    required this.isPinned,
    required this.appWindows,
    required this.onFocusWindow,
    required this.onNewWindow,
    required this.onTogglePin,
    required this.onOpenSettings,
    required this.onQuit,
  });

  final String appId;
  final String? appTitle;
  final bool isPinned;
  final List<DesktopWindowSummary> appWindows;
  final ValueChanged<String> onFocusWindow;
  final VoidCallback onNewWindow;
  final VoidCallback onTogglePin;
  final VoidCallback onOpenSettings;
  final VoidCallback onQuit;

  String get _appName {
    final custom = appTitle?.trim();
    if (custom != null && custom.isNotEmpty) return custom;
    return IcAppIcon.humanizeAppTitle(appId);
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(8, 4, 8, 6),
          child: Row(
            children: [
              IcAppIcon(appId: appId, size: 18),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  _appName,
                  overflow: TextOverflow.ellipsis,
                  style: UiType.textStyle(
                    12.5,
                    weight: FontWeight.w700,
                    color: c.textStrong,
                  ),
                ),
              ),
            ],
          ),
        ),
        if (appWindows.isNotEmpty) ...[
          for (final w in appWindows)
            _PopoverItem(
              icon: LucideIcons.appWindow,
              label: w.title,
              onTap: () => onFocusWindow(w.windowId),
            ),
          const _PopoverDivider(),
        ],
        _PopoverItem(
          key: const ValueKey('dock-menu-new-window'),
          icon: LucideIcons.plus,
          label: 'New window',
          onTap: onNewWindow,
        ),
        _PopoverItem(
          key: const ValueKey('dock-menu-pin'),
          icon: isPinned ? LucideIcons.pinOff : LucideIcons.pin,
          label: isPinned ? 'Unpin from Dock' : 'Pin to Dock',
          onTap: onTogglePin,
        ),
        _PopoverItem(
          key: const ValueKey('dock-menu-settings'),
          icon: LucideIcons.slidersHorizontal,
          label: 'App settings…',
          onTap: onOpenSettings,
        ),
        if (appWindows.isNotEmpty) ...[
          const _PopoverDivider(),
          _PopoverItem(
            key: const ValueKey('dock-menu-quit'),
            icon: LucideIcons.x,
            label: 'Quit $_appName',
            danger: true,
            onTap: onQuit,
          ),
        ],
      ],
    );
  }
}

class _PopoverDivider extends StatelessWidget {
  const _PopoverDivider();

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return Container(
      height: 1,
      margin: const EdgeInsets.symmetric(vertical: 6),
      color: c.borderHairline,
    );
  }
}

class _PopoverItem extends StatelessWidget {
  const _PopoverItem({
    super.key,
    required this.icon,
    required this.label,
    required this.onTap,
    this.trailing,
    this.danger = false,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final String? trailing;
  final bool danger;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final fg = danger ? c.error : c.textStrong;
    return _PopoverRow(
      onTap: onTap,
      child: SizedBox(
        height: 28,
        child: Row(
          children: [
            Icon(icon, size: 15, color: danger ? c.error : c.textMuted),
            const SizedBox(width: 9),
            Expanded(
              child: Text(
                label,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(13, weight: FontWeight.w500, color: fg),
              ),
            ),
            if (trailing != null)
              Text(trailing!, style: UiType.textStyle(12, color: c.textMuted)),
          ],
        ),
      ),
    );
  }
}

class _PopoverRow extends StatefulWidget {
  const _PopoverRow({super.key, required this.child, required this.onTap});

  final Widget child;
  final VoidCallback onTap;

  @override
  State<_PopoverRow> createState() => _PopoverRowState();
}

class _PopoverRowState extends State<_PopoverRow> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: widget.onTap,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
          decoration: BoxDecoration(
            color: _hover ? c.surfaceSunk : const Color(0x00000000),
            borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
          ),
          child: widget.child,
        ),
      ),
    );
  }
}
