import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import '../theme/brand_visuals.dart';
import '../theme/wire_node_values.dart';

enum _AbilityFilter {
  all('All', null),
  tools('Tools', WireModelAbility.tools),
  vision('Vision', WireModelAbility.vision),
  voice('Voice', WireModelAbility.voice);

  const _AbilityFilter(this.label, this.ability);

  final String label;
  final WireModelAbility? ability;
}

/// Composer model chip that opens a [ModelPickerPopover] anchored via
/// [MenuAnchor] when model choices are available.
class ModelPickerButton extends StatefulWidget {
  const ModelPickerButton({
    super.key,
    required this.models,
    this.activeModelId,
    this.activeModelLabel,
    this.activeProviderIcon,
    this.enabled = true,
    this.onSelect,
    this.onPin,
  });

  final List<ModelChoice> models;
  final String? activeModelId;
  final String? activeModelLabel;
  final String? activeProviderIcon;
  final bool enabled;
  final ValueChanged<String>? onSelect;
  final ValueChanged<String>? onPin;

  @override
  State<ModelPickerButton> createState() => _ModelPickerButtonState();
}

class _ModelPickerButtonState extends State<ModelPickerButton> {
  final _menu = MenuController();
  int _openEpoch = 0;

  bool get _canOpen =>
      widget.enabled &&
      widget.models.isNotEmpty &&
      (widget.onSelect != null || widget.onPin != null);

  void _toggle() {
    if (!_canOpen) return;
    if (_menu.isOpen) {
      _menu.close();
    } else {
      _menu.open();
    }
  }

  ModelChoice? _findActiveChoice() {
    final activeId = widget.activeModelId?.trim();
    if (activeId == null || activeId.isEmpty) return null;
    for (final choice in widget.models) {
      if (choice.id == activeId) return choice;
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final activeChoice = _findActiveChoice();
    final rawLabel = widget.activeModelLabel?.trim();
    final displayLabel = (rawLabel != null && rawLabel.isNotEmpty)
        ? rawLabel
        : (activeChoice?.label ?? 'Choose model');
    final rawIcon = widget.activeProviderIcon?.trim();
    final iconKey = (rawIcon != null && rawIcon.isNotEmpty)
        ? rawIcon
        : activeChoice?.icon;
    final spec = BrandVisuals.fromNode(label: displayLabel, iconKey: iconKey);

    if (!_canOpen) {
      return IcBrandChip(spec: spec, compact: true);
    }

    return MenuAnchor(
      controller: _menu,
      onOpen: () => setState(() => _openEpoch++),
      style: MenuStyle(
        backgroundColor: WidgetStatePropertyAll(c.surfaceCard),
        surfaceTintColor: const WidgetStatePropertyAll(Colors.transparent),
        padding: const WidgetStatePropertyAll(EdgeInsets.zero),
        elevation: const WidgetStatePropertyAll(8),
        shadowColor: WidgetStatePropertyAll(
          Colors.black.withValues(alpha: c.isDark ? 0.45 : 0.16),
        ),
        shape: WidgetStatePropertyAll(
          RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(UiTokens.of(context).panel),
            side: BorderSide(color: c.borderHairline),
          ),
        ),
      ),
      menuChildren: [
        ModelPickerPopover(
          key: ValueKey('model-picker-popover:$_openEpoch'),
          models: widget.models,
          activeModelId: widget.activeModelId,
          onSelect: widget.onSelect == null
              ? null
              : (id) {
                  _menu.close();
                  widget.onSelect!(id);
                },
          onPin: widget.onPin == null
              ? null
              : (id) {
                  _menu.close();
                  widget.onPin!(id);
                },
        ),
      ],
      builder: (context, controller, _) => Semantics(
        container: true,
        button: true,
        enabled: true,
        label: 'Choose model: $displayLabel',
        child: FocusableActionDetector(
          mouseCursor: SystemMouseCursors.click,
          actions: {
            ActivateIntent: CallbackAction<ActivateIntent>(
              onInvoke: (_) {
                _toggle();
                return null;
              },
            ),
          },
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTap: _toggle,
            child: _PickerChipTrigger(spec: spec, open: controller.isOpen),
          ),
        ),
      ),
    );
  }
}

class _PickerChipTrigger extends StatelessWidget {
  const _PickerChipTrigger({required this.spec, required this.open});

  final BrandVisualSpec spec;
  final bool open;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tint = spec.accent ?? c.accent;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: Color.alphaBlend(
          tint.withValues(alpha: c.isDark ? 0.12 : 0.08),
          c.surfaceSunk,
        ),
        borderRadius: BorderRadius.circular(999),
        border: Border.all(
          color: open
              ? c.accent
              : tint.withValues(alpha: c.isDark ? 0.28 : 0.22),
        ),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          IcBrandIcon(spec: spec, size: 13),
          const SizedBox(width: 5),
          Flexible(
            child: Text(
              spec.label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: UiType.textStyle(
                10.5,
                weight: FontWeight.w600,
                color: c.textStrong,
                height: 1.2,
              ),
            ),
          ),
          const SizedBox(width: 3),
          Icon(
            open ? LucideIcons.chevronUp : LucideIcons.chevronDown,
            size: 11,
            color: c.textMuted,
          ),
        ],
      ),
    );
  }
}

/// Popover body listing Local and Cloud model choices with an ability filter,
/// availability notes, pin-as-default action, and optional download progress.
class ModelPickerPopover extends StatefulWidget {
  const ModelPickerPopover({
    super.key,
    required this.models,
    this.activeModelId,
    this.onSelect,
    this.onPin,
  });

  final List<ModelChoice> models;
  final String? activeModelId;
  final ValueChanged<String>? onSelect;
  final ValueChanged<String>? onPin;

  @override
  State<ModelPickerPopover> createState() => _ModelPickerPopoverState();
}

class _ModelPickerPopoverState extends State<ModelPickerPopover> {
  _AbilityFilter _filter = _AbilityFilter.all;

  bool _matchesFilter(ModelChoice choice) {
    final requiredAbility = _filter.ability;
    if (requiredAbility == null) return true;
    final abilities = choice.abilities;
    if (abilities == null || abilities.isEmpty) return false;
    return abilities.any(
      (ability) => parseModelAbility(ability) == requiredAbility,
    );
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final viewport = MediaQuery.sizeOf(context);
    final width = math.min(340.0, math.max(240.0, viewport.width - 32));
    final maxListHeight = math.min(
      360.0,
      math.max(180.0, viewport.height * 0.5),
    );

    final filtered = [
      for (final choice in widget.models)
        if (_matchesFilter(choice)) choice,
    ];
    final local = [
      for (final choice in filtered)
        if (parseModelSection(choice.section) == WireModelSection.local) choice,
    ];
    final cloud = [
      for (final choice in filtered)
        if (parseModelSection(choice.section) == WireModelSection.cloud) choice,
    ];

    return SizedBox(
      width: width,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(10, 10, 10, 8),
            child: _AbilityFilterBar(
              selected: _filter,
              onChanged: (next) => setState(() => _filter = next),
            ),
          ),
          Container(height: 1, color: c.borderHairline),
          ConstrainedBox(
            constraints: BoxConstraints(maxHeight: maxListHeight),
            child: SingleChildScrollView(
              primary: false,
              padding: const EdgeInsets.symmetric(vertical: 6),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (local.isEmpty && cloud.isEmpty)
                    Padding(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 14,
                        vertical: 16,
                      ),
                      child: Text(
                        'No matching models',
                        style: UiType.textStyle(12.5, color: c.textMuted),
                      ),
                    ),
                  if (local.isNotEmpty) ...[
                    const _SectionHeader(title: 'Local'),
                    for (final choice in local)
                      _ModelChoiceRow(
                        choice: choice,
                        active: choice.id == widget.activeModelId,
                        onSelect: widget.onSelect,
                        onPin: widget.onPin,
                      ),
                  ],
                  if (cloud.isNotEmpty) ...[
                    if (local.isNotEmpty) const SizedBox(height: 6),
                    const _SectionHeader(title: 'Cloud'),
                    for (final choice in cloud)
                      _ModelChoiceRow(
                        choice: choice,
                        active: choice.id == widget.activeModelId,
                        onSelect: widget.onSelect,
                        onPin: widget.onPin,
                      ),
                  ],
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _AbilityFilterBar extends StatelessWidget {
  const _AbilityFilterBar({required this.selected, required this.onChanged});

  final _AbilityFilter selected;
  final ValueChanged<_AbilityFilter> onChanged;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return Container(
      padding: const EdgeInsets.all(3),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
        border: Border.all(color: c.borderHairline),
      ),
      child: Row(
        children: [
          for (final filter in _AbilityFilter.values)
            Expanded(
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onTap: () => onChanged(filter),
                  child: AnimatedContainer(
                    duration: UiTokens.of(context).fast,
                    padding: const EdgeInsets.symmetric(vertical: 5),
                    decoration: BoxDecoration(
                      color: filter == selected
                          ? c.surfaceCard
                          : Colors.transparent,
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).xs,
                      ),
                      border: filter == selected
                          ? Border.all(color: c.borderHairline)
                          : null,
                    ),
                    child: Center(
                      child: Text(
                        filter.label,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: UiType.textStyle(
                          11.5,
                          weight: filter == selected
                              ? FontWeight.w600
                              : FontWeight.w500,
                          color: filter == selected
                              ? c.textStrong
                              : c.textMuted,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _SectionHeader extends StatelessWidget {
  const _SectionHeader({required this.title});

  final String title;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 4, 12, 4),
      child: Text(
        title,
        style: UiType.textStyle(
          11,
          weight: FontWeight.w600,
          color: c.textSubtle,
        ),
      ),
    );
  }
}

class _ModelChoiceRow extends StatefulWidget {
  const _ModelChoiceRow({
    required this.choice,
    required this.active,
    this.onSelect,
    this.onPin,
  });

  final ModelChoice choice;
  final bool active;
  final ValueChanged<String>? onSelect;
  final ValueChanged<String>? onPin;

  @override
  State<_ModelChoiceRow> createState() => _ModelChoiceRowState();
}

class _ModelChoiceRowState extends State<_ModelChoiceRow> {
  bool _hovered = false;

  static (IconData, String)? _abilityIcon(String ability) =>
      switch (parseModelAbility(ability)) {
        WireModelAbility.tools => (LucideIcons.wrench, 'Tools'),
        WireModelAbility.vision => (LucideIcons.eye, 'Vision'),
        WireModelAbility.voice => (LucideIcons.mic, 'Voice'),
        WireModelAbility.reasoning => (LucideIcons.sparkles, 'Reasoning'),
        null => null,
      };

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final choice = widget.choice;
    final note = choice.note?.trim();
    final hasNote = note != null && note.isNotEmpty;
    final isInstallable = !choice.available && isInstallableModelNote(note);
    final canChoose =
        (choice.available || isInstallable) && widget.onSelect != null;
    final canPin = (choice.available || isInstallable) && widget.onPin != null;
    final spec = BrandVisuals.fromNode(
      label: choice.label,
      iconKey: choice.icon,
    );
    final abilities = [
      for (final ability in choice.abilities ?? const <String>[])
        ?_abilityIcon(ability),
    ];
    final progress = choice.progress;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
      child: MouseRegion(
        onEnter: canChoose ? (_) => setState(() => _hovered = true) : null,
        onExit: canChoose ? (_) => setState(() => _hovered = false) : null,
        child: Container(
          key: ValueKey('model-row:${choice.id}'),
          decoration: BoxDecoration(
            color: widget.active
                ? c.accentTint
                : (_hovered && canChoose ? c.surfaceSunk : Colors.transparent),
            borderRadius: BorderRadius.circular(UiTokens.of(context).sm),
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.center,
                children: [
                  Expanded(
                    child: MouseRegion(
                      cursor: canChoose
                          ? SystemMouseCursors.click
                          : SystemMouseCursors.basic,
                      child: GestureDetector(
                        behavior: HitTestBehavior.opaque,
                        onTap: canChoose
                            ? () => widget.onSelect!(choice.id)
                            : null,
                        child: Padding(
                          padding: const EdgeInsets.fromLTRB(8, 7, 6, 7),
                          child: Opacity(
                            opacity: choice.available ? 1.0 : 0.55,
                            child: Row(
                              crossAxisAlignment: CrossAxisAlignment.center,
                              children: [
                                IcBrandIcon(spec: spec, size: 16),
                                const SizedBox(width: 8),
                                Expanded(
                                  child: Column(
                                    mainAxisSize: MainAxisSize.min,
                                    crossAxisAlignment:
                                        CrossAxisAlignment.start,
                                    children: [
                                      Row(
                                        children: [
                                          Flexible(
                                            child: Text(
                                              choice.label,
                                              maxLines: 1,
                                              overflow: TextOverflow.ellipsis,
                                              style: UiType.textStyle(
                                                13,
                                                weight: widget.active
                                                    ? FontWeight.w600
                                                    : FontWeight.w500,
                                                color: c.textStrong,
                                              ),
                                            ),
                                          ),
                                          if (widget.active) ...[
                                            const SizedBox(width: 5),
                                            Icon(
                                              LucideIcons.check,
                                              size: 13,
                                              color: c.accent,
                                            ),
                                          ],
                                        ],
                                      ),
                                      if (hasNote) ...[
                                        const SizedBox(height: 1),
                                        Text(
                                          note,
                                          maxLines: 2,
                                          overflow: TextOverflow.ellipsis,
                                          style: UiType.textStyle(
                                            11.5,
                                            color: c.textMuted,
                                          ),
                                        ),
                                      ],
                                    ],
                                  ),
                                ),
                                if (abilities.isNotEmpty) ...[
                                  const SizedBox(width: 6),
                                  Row(
                                    mainAxisSize: MainAxisSize.min,
                                    children: [
                                      for (
                                        var i = 0;
                                        i < abilities.length;
                                        i++
                                      ) ...[
                                        if (i > 0) const SizedBox(width: 4),
                                        Tooltip(
                                          message: abilities[i].$2,
                                          child: Icon(
                                            abilities[i].$1,
                                            size: 12.5,
                                            color: c.textMuted,
                                          ),
                                        ),
                                      ],
                                    ],
                                  ),
                                ],
                              ],
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
                  if (isInstallable && widget.onSelect != null)
                    Padding(
                      padding: const EdgeInsets.only(right: 4),
                      child: Tooltip(
                        message: 'Install ${choice.label}',
                        child: Semantics(
                          container: true,
                          button: true,
                          enabled: true,
                          label: 'Install ${choice.label}',
                          child: MouseRegion(
                            cursor: SystemMouseCursors.click,
                            child: GestureDetector(
                              key: ValueKey('model-install:${choice.id}'),
                              behavior: HitTestBehavior.opaque,
                              onTap: () => widget.onSelect!(choice.id),
                              child: Container(
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 7,
                                  vertical: 4,
                                ),
                                decoration: BoxDecoration(
                                  color: c.accentTint,
                                  borderRadius: BorderRadius.circular(
                                    UiTokens.of(context).xs,
                                  ),
                                  border: Border.all(
                                    color: c.accent.withValues(alpha: 0.35),
                                  ),
                                ),
                                child: Row(
                                  mainAxisSize: MainAxisSize.min,
                                  children: [
                                    Icon(
                                      LucideIcons.download,
                                      size: 11.5,
                                      color: c.accent,
                                    ),
                                    const SizedBox(width: 4),
                                    Text(
                                      'Install',
                                      style: UiType.textStyle(
                                        11,
                                        weight: FontWeight.w600,
                                        color: c.accent,
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                  if (widget.onPin != null)
                    Padding(
                      padding: const EdgeInsets.only(right: 4),
                      child: Tooltip(
                        message: 'Make default',
                        child: Semantics(
                          container: true,
                          button: true,
                          enabled: canPin,
                          label: 'Make ${choice.label} default',
                          child: MouseRegion(
                            cursor: canPin
                                ? SystemMouseCursors.click
                                : MouseCursor.defer,
                            child: GestureDetector(
                              key: ValueKey('model-pin:${choice.id}'),
                              behavior: HitTestBehavior.opaque,
                              onTap: canPin
                                  ? () => widget.onPin!(choice.id)
                                  : null,
                              child: SizedBox(
                                width: 28,
                                height: 28,
                                child: Center(
                                  child: Icon(
                                    LucideIcons.pin,
                                    size: 13,
                                    color: canPin
                                        ? c.textMuted
                                        : c.textSubtle.withValues(alpha: 0.45),
                                  ),
                                ),
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                ],
              ),
              if (progress != null)
                Padding(
                  padding: const EdgeInsets.fromLTRB(8, 0, 8, 5),
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(999),
                    child: LinearProgressIndicator(
                      value: progress.isNaN ? null : progress.clamp(0.0, 1.0),
                      minHeight: 3,
                      color: c.accent,
                      backgroundColor: c.surfaceSunk,
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
