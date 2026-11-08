part of '../neuron_view.dart';

extension _UiKitSettingsAdapter on ImmutableView {
  static IconData _settingsIconFor(String tabKey) {
    final stripped = tabKey.startsWith('tab-') ? tabKey.substring(4) : tabKey;
    return switch (stripped) {
      'general' => LucideIcons.slidersHorizontal,
      'models' => LucideIcons.cpu,
      'keys' => LucideIcons.keyRound,
      'connections' => LucideIcons.plug,
      'modules' => LucideIcons.boxes,
      'apps' => LucideIcons.layoutGrid,
      'variables' => LucideIcons.braces,
      'notifications' => LucideIcons.bell,
      'archived' => LucideIcons.archive,
      'account' => LucideIcons.userCheck,
      'danger' => LucideIcons.triangleAlert,
      'billing' => LucideIcons.creditCard,
      _ => uiIcon(stripped) ?? LucideIcons.settings,
    };
  }

  Widget _buildSettingsDesktopView(
    BuildContext context,
    Map node,
    List<Map> children,
    List<Widget> widgets,
    int depth,
  ) {
    return LayoutBuilder(
      builder: (context, constraints) {
        if (constraints.maxWidth < 680) {
          return layout(
            {
              'mode': 'column',
              'extents': node['extents'],
              'gap': node['gap'],
              'followEnd': node['followEnd'],
            },
            widgets,
            children,
          );
        }
        final c = UiColors.of(context);
        final tabItems = (children[0]['children'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        final bodyNode = children[1];
        final bodyKey = bodyNode['key'] as String? ?? '';
        final bodyChildren = (bodyNode['children'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        final bodyWidgets = [
          for (final child in bodyChildren) _node(context, child, depth + 2),
        ];

        final headingText = bodyChildren.isNotEmpty
            ? bodyChildren[0]['text'] as String? ?? ''
            : '';
        final descriptionText = bodyChildren.length > 1
            ? bodyChildren[1]['text'] as String? ?? ''
            : '';
        final restWidgets = bodyWidgets.length > 2
            ? bodyWidgets.sublist(2)
            : bodyWidgets;
        final currentTheme = localPreferences?.read(PreferenceStore.localTheme);

        return Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Container(
              width: 232,
              decoration: BoxDecoration(
                color: c.recessed,
                border: Border(right: BorderSide(color: c.hairline)),
              ),
              padding: const EdgeInsets.fromLTRB(12, 16, 12, 16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Padding(
                    padding: const EdgeInsets.fromLTRB(8, 0, 8, 14),
                    child: Row(
                      children: [
                        Container(
                          width: 26,
                          height: 26,
                          decoration: BoxDecoration(
                            color: c.accentTint,
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: Icon(
                            LucideIcons.settings,
                            size: 14,
                            color: c.accent,
                          ),
                        ),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                'System Settings',
                                style: UiType.textStyle(
                                  13,
                                  weight: FontWeight.w700,
                                  color: c.strong,
                                ),
                              ),
                              Text(
                                'neuron://local/apps/settings',
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: UiType.tabularStyle(
                                  10.5,
                                  color: c.muted,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                  Expanded(
                    child: ListView.separated(
                      itemCount: tabItems.length,
                      separatorBuilder: (_, _) => const SizedBox(height: 2),
                      itemBuilder: (context, index) {
                        final tab = tabItems[index];
                        final tabKey = tab['key'] as String? ?? 'tab-$index';
                        final label = tab['text'] as String? ?? '';
                        final selected = tab['selected'] == true;
                        final onTap = _dispatchButton(context, tab);
                        final isDanger = tabKey == 'tab-danger';
                        return KeyedSubtree(
                          key: ValueKey('view:$name:$tabKey'),
                          child: GestureDetector(
                            onTap: onTap,
                            child: MouseRegion(
                              cursor: onTap != null
                                  ? SystemMouseCursors.click
                                  : SystemMouseCursors.basic,
                              child: AnimatedContainer(
                                duration: IcMotion.fast,
                                height: 34,
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 10,
                                ),
                                decoration: BoxDecoration(
                                  color: selected
                                      ? c.accentTint
                                      : Colors.transparent,
                                  borderRadius: BorderRadius.circular(8),
                                ),
                                child: Row(
                                  children: [
                                    Icon(
                                      _settingsIconFor(tabKey),
                                      size: 15,
                                      color: isDanger
                                          ? c.error
                                          : (selected ? c.accent : c.muted),
                                    ),
                                    const SizedBox(width: 10),
                                    Expanded(
                                      child: Text(
                                        label,
                                        maxLines: 1,
                                        overflow: TextOverflow.ellipsis,
                                        style: UiType.textStyle(
                                          13,
                                          weight: selected
                                              ? FontWeight.w600
                                              : FontWeight.w500,
                                          color: onTap == null && !selected
                                              ? c.subtle
                                              : (isDanger
                                                    ? c.error
                                                    : (selected
                                                          ? c.strong
                                                          : c.body)),
                                        ),
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                            ),
                          ),
                        );
                      },
                    ),
                  ),
                ],
              ),
            ),
            Expanded(
              child: ColoredBox(
                color: c.page,
                child: SingleChildScrollView(
                  key: ValueKey('settings-scroll:$bodyKey'),
                  padding: const EdgeInsets.fromLTRB(36, 28, 36, 48),
                  child: Align(
                    alignment: Alignment.topLeft,
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 820),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          if (headingText.isNotEmpty)
                            Text(
                              headingText,
                              style: UiType.displayStyle(
                                22,
                                weight: FontWeight.w700,
                                color: c.strong,
                              ),
                            ),
                          if (descriptionText.isNotEmpty) ...[
                            const SizedBox(height: 6),
                            Text(
                              descriptionText,
                              style: UiType.textStyle(13, color: c.muted),
                            ),
                          ],
                          const SizedBox(height: 20),
                          if (bodyKey == 'tab-body-general' &&
                              localPreferences != null) ...[
                            Text(
                              'UI IDENTITY & GLASS MATERIALITY',
                              style: UiType.textStyle(
                                11,
                                weight: FontWeight.w700,
                                color: c.muted,
                              ),
                            ),
                            const SizedBox(height: 10),
                            Wrap(
                              spacing: 10,
                              runSpacing: 10,
                              children: [
                                for (final preset in IcStylePreset.values)
                                  _buildPresetCard(
                                    context,
                                    c,
                                    preset,
                                    currentTheme == preset.id ||
                                        (currentTheme == 'intochat' &&
                                            preset ==
                                                IcStylePreset.fluentAcrylic),
                                  ),
                              ],
                            ),
                            const SizedBox(height: 22),
                          ],
                          UiKitSurface(
                            padding: const EdgeInsets.all(20),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              spacing: 14,
                              children: restWidgets,
                            ),
                          ),
                          if (widgets.length > 2) ...[
                            const SizedBox(height: 14),
                            for (var i = 2; i < widgets.length; i++) widgets[i],
                          ],
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ],
        );
      },
    );
  }

  Widget _buildPresetCard(
    BuildContext context,
    UiColors c,
    IcStylePreset preset,
    bool active,
  ) {
    final palette = IcColors.forPreset(preset, dark: true);
    return GestureDetector(
      onTap: () => unawaited(
        localPreferences?.write(PreferenceStore.localTheme, preset.id) ??
            Future.value(),
      ),
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: AnimatedContainer(
          duration: IcMotion.fast,
          width: 152,
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: active ? c.accentTint : c.surface,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: active ? c.accent : c.hairline,
              width: active ? 1.5 : 1,
            ),
            boxShadow: active ? UiShadows.sm(c) : null,
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Container(
                    width: 18,
                    height: 18,
                    decoration: BoxDecoration(
                      color: palette.surfacePage,
                      borderRadius: BorderRadius.circular(5),
                      border: Border.all(color: palette.borderInput),
                    ),
                    alignment: Alignment.center,
                    child: Container(
                      width: 8,
                      height: 8,
                      decoration: BoxDecoration(
                        color: palette.accent,
                        shape: BoxShape.circle,
                      ),
                    ),
                  ),
                  const Spacer(),
                  if (palette.isGlass)
                    const TagPill(label: 'Glass', tone: TagTone.accent),
                ],
              ),
              const SizedBox(height: 8),
              Text(
                preset.label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(
                  12.5,
                  weight: FontWeight.w600,
                  color: c.strong,
                ),
              ),
              const SizedBox(height: 2),
              Text(
                preset.description,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(11, color: c.muted),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
