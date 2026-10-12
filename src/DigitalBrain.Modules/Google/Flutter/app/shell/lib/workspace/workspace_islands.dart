import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';

import 'workspace_store.dart';

// The bottom bar: Home | the dock | user and compute. The dock is the focused app's toolbar,
// handed in by the workspace; nothing here knows what it holds.
class WorkspaceIslands extends StatelessWidget {
  const WorkspaceIslands({
    super.key,
    required this.store,
    required this.dock,
    required this.onNewWorkspace,
    required this.onSavedWork,
    required this.onSearch,
    required this.onSettings,
    required this.onHome,
    this.onConfiguration,
    this.onConnections,
    this.onCompute,
    this.onSwitchAccount,
    this.home = false,
  });
  final WorkspaceStore store;
  final Widget dock;
  final VoidCallback onNewWorkspace, onSavedWork, onSearch, onSettings, onHome;
  final VoidCallback? onCompute, onConfiguration, onConnections;
  final Future<void> Function()? onSwitchAccount;
  final bool home;

  static Widget island(BuildContext context, Widget child) => ClipRRect(
    borderRadius: BorderRadius.circular(UiTokens.of(context).surfaceRadius),
    child: BackdropFilter(
      filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
      child: DecoratedBox(
        decoration: BoxDecoration(
          color: Theme.of(context).colorScheme.surface.withValues(alpha: .9),
          border: Border.all(
            color: Theme.of(context).colorScheme.outlineVariant
                .withValues(alpha: .45),
          ),
          borderRadius: BorderRadius.circular(
            UiTokens.of(context).surfaceRadius,
          ),
          boxShadow: [
            BoxShadow(
              color: Colors.black.withValues(alpha: .14),
              blurRadius: 24,
              offset: const Offset(0, 6),
            ),
          ],
        ),
        child: Padding(
          padding: EdgeInsets.symmetric(
            horizontal: UiTokens.of(context).spaceSmall,
            vertical: UiTokens.of(context).spaceSmall / 2,
          ),
          child: child,
        ),
      ),
    ),
  );

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, c) {
      final narrow = c.maxWidth < 900;
      final project = store.currentProject;
      final homeIsland = island(
        context,
        IconButton(
          key: const Key('home-island'),
          tooltip: 'Home',
          onPressed: onHome,
          icon: Icon(
            Icons.apps_rounded,
            size: 23,
            color: home ? Theme.of(context).colorScheme.primary : null,
          ),
        ),
      );
      final account = island(
        context,
        Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            IconButton(
              key: const Key('compute-usage-button'),
              tooltip: 'Compute',
              onPressed: onCompute,
              icon: const Icon(Icons.speed_outlined, size: 18),
            ),
            IconButton(
              tooltip: 'Search',
              onPressed: onSearch,
              icon: const Icon(Icons.search, size: 21),
            ),
            if (!narrow)
              IconButton(
                tooltip: 'Settings',
                onPressed: onSettings,
                icon: const Icon(Icons.settings_outlined, size: 20),
              ),
            PopupMenuButton<String>(
              tooltip: 'Profile',
              position: PopupMenuPosition.over,
              icon: CircleAvatar(
                radius: 14,
                backgroundColor: Theme.of(context).colorScheme.primary
                    .withValues(alpha: .15),
                child: Icon(
                  Icons.person_outline,
                  size: 18,
                  color: Theme.of(context).colorScheme.primary,
                ),
              ),
              onSelected: (value) {
                switch (value) {
                  case 'switch-account':
                    onSwitchAccount?.call();
                  case 'settings':
                    onSettings();
                  case 'configuration':
                    onConfiguration?.call();
                  case 'new':
                    onNewWorkspace();
                  case 'saved':
                    onSavedWork();
                  default:
                    store.selectProject(value);
                }
              },
              itemBuilder: (_) => [
                PopupMenuItem(
                  enabled: false,
                  child: Text(
                    store.settings.displayName.isEmpty
                        ? 'Personal account'
                        : store.settings.displayName,
                  ),
                ),
                const PopupMenuDivider(),
                if (onSwitchAccount != null)
                  const PopupMenuItem(
                    value: 'switch-account',
                    child: ListTile(
                      leading: Icon(Icons.switch_account_outlined),
                      title: Text('Switch account'),
                      contentPadding: EdgeInsets.zero,
                    ),
                  ),
                for (final p in store.projects)
                  CheckedPopupMenuItem(
                    value: p.id,
                    checked: p.id == project.id,
                    child: Text(p.title),
                  ),
                const PopupMenuItem(value: 'new', child: Text('New workspace')),
                const PopupMenuItem(value: 'saved', child: Text('Saved work')),
                const PopupMenuDivider(),
                const PopupMenuItem(value: 'settings', child: Text('Settings')),
                if (onConfiguration != null)
                  const PopupMenuItem(
                    value: 'configuration',
                    child: Text('Workspace configuration'),
                  ),
              ],
            ),
          ],
        ),
      );
      return Padding(
        padding: EdgeInsets.symmetric(
          horizontal: UiTokens.of(context).spaceMedium,
          vertical: UiTokens.of(context).spaceSmall,
        ),
        child: Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          children: [
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                homeIsland,
                if (onConnections != null)
                  IconButton(
                    tooltip: 'Connections',
                    onPressed: onConnections,
                    icon: const Icon(Icons.cable_outlined),
                  ),
              ],
            ),
            Flexible(
              child: Padding(
                padding: EdgeInsets.symmetric(
                  horizontal: UiTokens.of(context).spaceSmall,
                ),
                child: SingleChildScrollView(
                  scrollDirection: Axis.horizontal,
                  child: dock,
                ),
              ),
            ),
            account,
          ],
        ),
      );
    },
  );
}
