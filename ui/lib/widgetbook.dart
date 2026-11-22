import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
// ignore: depend_on_referenced_packages
import 'package:widgetbook/widgetbook.dart';

import 'src/widgets/ino/ino_companion_orb.dart';
import 'src/widgets/ino/ino_orb_v2.dart';
import 'src/widgets/neuron/ui_control_surface.dart';

/// Free, open-source IntoChat.OS UI Kit & Neuron Widgetbook workbench.
///
/// Run locally with hot reload:
///   `flutter run -d windows -t lib/widgetbook.dart`
///   `flutter run -d web-server --web-port 8090 -t lib/widgetbook.dart`
///
/// Build static web bundle for Kernel `/widgetbook`:
///   `flutter build web -t lib/widgetbook.dart --base-href /widgetbook/ -o build/web-widgetbook`
Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final catalog = await _loadShowcaseCatalog();
  runApp(IntoChatWidgetbookApp(catalog: catalog));
}

Future<List<Map<String, dynamic>>> _loadShowcaseCatalog() async {
  try {
    final raw = await rootBundle.loadString(
      'lib/src/generated/showcase_catalog.json',
    );
    return (jsonDecode(raw) as List).cast<Map<String, dynamic>>();
  } catch (_) {
    return const [];
  }
}

final class IntoChatWidgetbookApp extends StatefulWidget {
  const IntoChatWidgetbookApp({super.key, required this.catalog});

  final List<Map<String, dynamic>> catalog;

  @override
  State<IntoChatWidgetbookApp> createState() => _IntoChatWidgetbookAppState();
}

enum _PreviewViewport {
  full('Full', null),
  desktop('Desktop', Size(1280, 800)),
  tablet('Tablet', Size(768, 1024)),
  phone('Phone', Size(390, 844));

  const _PreviewViewport(this.label, this.size);

  final String label;
  final Size? size;

  String get caption => size == null
      ? label
      : '$label · ${size!.width.toInt()}×${size!.height.toInt()}';
}

class _IntoChatWidgetbookAppState extends State<IntoChatWidgetbookApp> {
  UiThemeSeeds _seeds = UiThemeSeeds.warmDark;
  bool _studioOpen = false;
  _PreviewViewport _viewport = _PreviewViewport.full;

  Widget _wrapViewport(BuildContext context, UiColors colors, Widget child) {
    final size = _viewport.size;
    if (size == null) return child;
    final mq = MediaQuery.of(context);
    return Container(
      color: colors.recessed,
      padding: const EdgeInsets.all(16),
      child: Column(
        children: [
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: UiBadge(
              label: _viewport.caption,
              tone: UiBadgeTone.accent,
              dot: true,
            ),
          ),
          Expanded(
            child: Center(
              child: FittedBox(
                fit: BoxFit.scaleDown,
                child: Container(
                  width: size.width,
                  height: size.height,
                  decoration: BoxDecoration(
                    color: colors.page,
                    borderRadius: BorderRadius.circular(16),
                    border: Border.all(color: colors.borderStrong, width: 1.5),
                    boxShadow: [
                      BoxShadow(
                        color: Colors.black.withValues(alpha: 0.25),
                        blurRadius: 24,
                        offset: const Offset(0, 8),
                      ),
                    ],
                  ),
                  clipBehavior: Clip.antiAlias,
                  child: MediaQuery(
                    data: mq.copyWith(size: size),
                    child: child,
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final catalogComponents = <WidgetbookComponent>[
      for (final entry in widget.catalog)
        if ((entry['examples'] as List?)?.isNotEmpty == true)
          WidgetbookComponent(
            name:
                '${entry['kind']}${entry['isNeuron'] == true ? ' (Neuron)' : ''}',
            useCases: [
              for (var i = 0; i < (entry['examples'] as List).length; i++)
                WidgetbookUseCase(
                  name: 'Example ${i + 1}',
                  builder: (ctx) => _CatalogNodePreview(
                    example: ((entry['examples'] as List)[i] as Map)
                        .cast<String, dynamic>(),
                  ),
                ),
            ],
          ),
    ];

    return Widgetbook.material(
      appBuilder: (context, child) {
        final colors = _seeds.toUiColors();
        return UiSeedThemeScope(
          seeds: _seeds,
          child: Material(
            color: colors.page,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 6,
                  ),
                  decoration: BoxDecoration(
                    color: colors.elevated,
                    border: Border(bottom: BorderSide(color: colors.hairline)),
                  ),
                  child: Row(
                    children: [
                      Text(
                        'IntoChat.OS Widgetbook',
                        style: TextStyle(
                          color: colors.strong,
                          fontSize: 12.5,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Align(
                          alignment: Alignment.centerRight,
                          child: SingleChildScrollView(
                            scrollDirection: Axis.horizontal,
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                UiSegmentedControl<_PreviewViewport>(
                                  compact: true,
                                  semanticsId: 'widgetbook:viewport-preview',
                                  value: _viewport,
                                  onChanged: (next) =>
                                      setState(() => _viewport = next),
                                  options: const [
                                    UiSegmentedOption(
                                      value: _PreviewViewport.full,
                                      label: 'Full',
                                      icon: LucideIcons.maximize2,
                                    ),
                                    UiSegmentedOption(
                                      value: _PreviewViewport.desktop,
                                      label: 'Desktop',
                                      icon: LucideIcons.monitor,
                                    ),
                                    UiSegmentedOption(
                                      value: _PreviewViewport.tablet,
                                      label: 'Tablet',
                                      icon: LucideIcons.tablet,
                                    ),
                                    UiSegmentedOption(
                                      value: _PreviewViewport.phone,
                                      label: 'Phone',
                                      icon: LucideIcons.smartphone,
                                    ),
                                  ],
                                ),
                                const SizedBox(width: 10),
                                UiMixChip(
                                  label: _studioOpen
                                      ? 'Hide Theme Studio'
                                      : 'Theme Studio',
                                  selected: _studioOpen,
                                  semanticsId: 'widgetbook:theme-studio-toggle',
                                  style: const UiStyleData(
                                    tone: 'accent',
                                    paddingX: 10,
                                    paddingY: 4,
                                    radius: 999,
                                  ),
                                  onSelected: (next) =>
                                      setState(() => _studioOpen = next),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
                if (_studioOpen)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(12, 10, 12, 4),
                    child: ThemeStudioPanel(
                      seeds: _seeds,
                      onChanged: (next) => setState(() => _seeds = next),
                    ),
                  ),
                Expanded(child: _wrapViewport(context, colors, child)),
              ],
            ),
          ),
        );
      },
      addons: [
        TextScaleAddon(min: 0.85, max: 1.5),
        AlignmentAddon(initialAlignment: Alignment.center),
      ],
      directories: [
        WidgetbookFolder(
          name: 'Mix Primitives',
          children: [
            WidgetbookComponent(
              name: 'Button',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiMixActionButton (Tones & States)',
                  builder: (_) => const _MixActionButtonPlayground(),
                ),
                WidgetbookUseCase(
                  name: 'UiMixButton (C# UiStyle Parity)',
                  builder: (_) => const _MixButtonPlayground(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'Field',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiMixField (States & Modes)',
                  builder: (_) => const _MixFieldPlayground(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'Select',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiMixSelect (Dropdown & Searchable)',
                  builder: (_) => const _MixSelectPlayground(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'Badge',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiBadge (Tones, Icons & Dots)',
                  builder: (_) => const _BadgePlayground(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'Segmented Control',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiSegmentedControl (Interactive)',
                  builder: (_) => const _SegmentedControlPlayground(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'Adaptive Sheet',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiSheet & showUiSheet',
                  builder: (_) => const _AdaptiveSheetPlayground(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'Skeleton',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiSkeleton (Pulse Placeholders)',
                  builder: (_) => const _SkeletonPlayground(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'Adaptive Nav Bar',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiAdaptiveNavBar (Rail ↔ Bottom Bar)',
                  builder: (_) => const _AdaptiveNavBarPlayground(),
                ),
              ],
            ),
          ],
        ),
        WidgetbookFolder(
          name: 'Foundations',
          children: [
            WidgetbookComponent(
              name: 'Typography',
              useCases: [
                WidgetbookUseCase(
                  name: 'Type Scale & Fonts',
                  builder: (_) => const _TypographyShowcase(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'Iconography',
              useCases: [
                WidgetbookUseCase(
                  name: 'Lucide Icon Catalog',
                  builder: (_) => const _IconographyShowcase(),
                ),
              ],
            ),
            WidgetbookComponent(
              name: 'WinForms Interaction & Motion',
              useCases: [
                WidgetbookUseCase(
                  name: 'UiControlSurface Playground',
                  builder: (_) => const _ControlSurfacePlayground(),
                ),
                WidgetbookUseCase(
                  name: 'Mix + NakedButton (C# UiStyle Parity)',
                  builder: (_) => const _MixButtonPlayground(),
                ),
                WidgetbookUseCase(
                  name: 'Ino Orb v2 (S10 Flying 3D Lottie Sphere)',
                  builder: (_) => const _InoOrbV2Playground(),
                ),
                WidgetbookUseCase(
                  name: 'Ino Companion Orb (S12c Floating Transparent)',
                  builder: (_) => const _InoCompanionPlayground(),
                ),
              ],
            ),
          ],
        ),
        WidgetbookFolder(
          name: 'C# UI Kit & Neurons (Auto-Catalog)',
          children: catalogComponents,
        ),
      ],
    );
  }
}

final class _CatalogNodePreview extends StatefulWidget {
  const _CatalogNodePreview({required this.example});

  final Map<String, dynamic> example;

  @override
  State<_CatalogNodePreview> createState() => _CatalogNodePreviewState();
}

class _CatalogNodePreviewState extends State<_CatalogNodePreview> {
  final List<String> _events = [];

  @override
  Widget build(BuildContext context) {
    final props = widget.example['props'];
    final ctx = NodeContext(
      view: 'local/apps/widgetbook/view',
      generation: 1,
      slotProps: props is Map ? props.cast<String, dynamic>() : const {},
      send: (event) async {
        setState(() {
          _events.insert(0, jsonEncode(event));
          if (_events.length > 5) _events.removeLast();
        });
        return null;
      },
    );
    final node = Node.fromJson(widget.example);
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Center(child: NodeRenderer.render(node, ctx)),
          if (_events.isNotEmpty) ...[
            const SizedBox(height: 16),
            Text(
              'Emitted Signals / Commands:',
              style: Theme.of(context).textTheme.labelMedium,
            ),
            const SizedBox(height: 6),
            for (final ev in _events)
              Text(
                ev,
                style: const TextStyle(
                  fontFamily: 'JetBrains Mono',
                  fontSize: 12,
                ),
              ),
          ],
        ],
      ),
    );
  }
}

final class _TypographyShowcase extends StatelessWidget {
  const _TypographyShowcase();

  @override
  Widget build(BuildContext context) {
    final tt = Theme.of(context).textTheme;
    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Display Large · InterDisplay', style: tt.displaySmall),
          const SizedBox(height: 8),
          Text('Headline Medium · InterDisplay', style: tt.headlineMedium),
          const SizedBox(height: 8),
          Text('Title Medium · Inter SemiBold', style: tt.titleMedium),
          const SizedBox(height: 8),
          Text('Body Medium · Inter Regular', style: tt.bodyMedium),
          const SizedBox(height: 8),
          Text('Label Small · Inter Medium', style: tt.labelSmall),
          const SizedBox(height: 12),
          const Text(
            'Code · JetBrains Mono — const surface = UiControlSurface();',
            style: TextStyle(fontFamily: 'JetBrains Mono', fontSize: 13),
          ),
        ],
      ),
    );
  }
}

final class _IconographyShowcase extends StatelessWidget {
  const _IconographyShowcase();

  @override
  Widget build(BuildContext context) {
    final entries = uiIconCatalog.take(48).toList();
    return GridView.builder(
      padding: const EdgeInsets.all(24),
      gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
        maxCrossAxisExtent: 120,
        mainAxisSpacing: 12,
        crossAxisSpacing: 12,
      ),
      itemCount: entries.length,
      itemBuilder: (context, index) {
        final e = entries[index];
        return Card(
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(e.icon, size: 22),
              const SizedBox(height: 6),
              Text(
                e.slug,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context).textTheme.labelSmall,
              ),
            ],
          ),
        );
      },
    );
  }
}

final class _ControlSurfacePlayground extends StatefulWidget {
  const _ControlSurfacePlayground();

  @override
  State<_ControlSurfacePlayground> createState() =>
      _ControlSurfacePlaygroundState();
}

class _ControlSurfacePlaygroundState extends State<_ControlSurfacePlayground> {
  final List<String> _log = [];

  @override
  Widget build(BuildContext context) {
    final colors = UiColors.of(context);
    final preset = context.knobs.object.dropdown<UiMotionPreset>(
      label: 'Motion Preset',
      options: UiMotionPreset.values,
      initialOption: UiMotionPreset.spring,
      labelBuilder: (p) => p.name,
    );
    final enabled = context.knobs.boolean(label: 'Enabled', initialValue: true);

    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          UiControlSurface(
            enabled: enabled,
            eventMask: UiEventMask.all,
            motion: preset,
            onEmit: (method, args) async {
              setState(() {
                _log.insert(0, '$method(${args.length} arg)');
                if (_log.length > 6) _log.removeLast();
              });
            },
            builder: (context, state) {
              return Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 24,
                  vertical: 14,
                ),
                decoration: BoxDecoration(
                  color: state.isPressed
                      ? colors.accent.withValues(alpha: 0.25)
                      : state.isHovered
                      ? colors.accent.withValues(alpha: 0.14)
                      : colors.surface,
                  borderRadius: BorderRadius.circular(10),
                  border: Border.all(
                    color: state.isHovered ? colors.accent : colors.hairline,
                  ),
                ),
                child: Text(
                  'Hover / Press / Focus Me (${state.isPressed
                      ? "Pressed"
                      : state.isHovered
                      ? "Hovered"
                      : "Idle"})',
                ),
              );
            },
          ),
          const SizedBox(height: 16),
          for (final item in _log)
            Text(
              item,
              style: const TextStyle(
                fontFamily: 'JetBrains Mono',
                fontSize: 12,
              ),
            ),
        ],
      ),
    );
  }
}

final class _MixButtonPlayground extends StatefulWidget {
  const _MixButtonPlayground();

  @override
  State<_MixButtonPlayground> createState() => _MixButtonPlaygroundState();
}

class _MixButtonPlaygroundState extends State<_MixButtonPlayground> {
  final List<String> _events = [];

  @override
  Widget build(BuildContext context) {
    final label = context.knobs.string(
      label: 'Label',
      initialValue: 'Deploy Neuron',
    );
    final paddingX = context.knobs.double.slider(
      label: 'C# PaddingX (px)',
      min: 4,
      max: 48,
      initialValue: 20,
    );
    final paddingY = context.knobs.double.slider(
      label: 'C# PaddingY (px)',
      min: 4,
      max: 28,
      initialValue: 12,
    );
    final radius = context.knobs.double.slider(
      label: 'C# Radius (px)',
      min: 0,
      max: 24,
      initialValue: 10,
    );
    final tone = context.knobs.object.dropdown<String>(
      label: 'C# SurfaceTone',
      options: const [
        'accent',
        'success',
        'warning',
        'danger',
        'subtle',
        'card',
      ],
      initialOption: 'accent',
    );
    final motion = context.knobs.object.dropdown<UiMotionPreset>(
      label: 'C# MotionPreset',
      options: UiMotionPreset.values,
      initialOption: UiMotionPreset.spring,
      labelBuilder: (p) => p.name,
    );
    final enabled = context.knobs.boolean(label: 'Enabled', initialValue: true);

    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          UiMixButton(
            label: label,
            enabled: enabled,
            eventMask: UiEventMask.all,
            semanticsId: 'widgetbook:mix-button',
            style: UiStyleData(
              tone: tone,
              paddingX: paddingX,
              paddingY: paddingY,
              radius: radius,
              motion: motion,
            ),
            onPressed: () {
              setState(() {
                _events.insert(0, 'OnClick("$label")');
                if (_events.length > 6) _events.removeLast();
              });
            },
            onEmit: (method, _) async {
              setState(() {
                _events.insert(0, method);
                if (_events.length > 6) _events.removeLast();
              });
            },
          ),
          const SizedBox(height: 16),
          for (final e in _events)
            Text(
              e,
              style: const TextStyle(
                fontFamily: 'JetBrains Mono',
                fontSize: 12,
              ),
            ),
        ],
      ),
    );
  }
}

final class _InoOrbV2Playground extends StatefulWidget {
  const _InoOrbV2Playground();

  @override
  State<_InoOrbV2Playground> createState() => _InoOrbV2PlaygroundState();
}

class _InoOrbV2PlaygroundState extends State<_InoOrbV2Playground> {
  Offset _target = const Offset(180, 120);
  String _status = 'Ready · Ask me about Auth';

  @override
  Widget build(BuildContext context) {
    final state = context.knobs.object.dropdown<PresenceState>(
      label: 'PresenceState',
      options: PresenceState.values,
      initialOption: PresenceState.listening,
      labelBuilder: (s) => s.name,
    );
    final audioLevel = context.knobs.double.slider(
      label: 'Voice Mic Amplitude',
      min: 0.0,
      max: 1.0,
      initialValue: 0.35,
    );
    final size = context.knobs.double.slider(
      label: 'Orb Size (px)',
      min: 48,
      max: 148,
      initialValue: 88,
    );

    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(12),
          child: Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              Semantics(
                identifier: 'ino:fly-top-left',
                button: true,
                child: UiMixButton(
                  label: 'Fly → Top-Left',
                  style: const UiStyleData(
                    tone: 'subtle',
                    paddingX: 12,
                    paddingY: 6,
                    radius: 8,
                  ),
                  onPressed: () => setState(() {
                    _target = const Offset(36, 36);
                    _status = 'Inspecting Auth Tree…';
                  }),
                ),
              ),
              Semantics(
                identifier: 'ino:fly-center',
                button: true,
                child: UiMixButton(
                  label: 'Fly → Center',
                  style: const UiStyleData(
                    tone: 'accent',
                    paddingX: 12,
                    paddingY: 6,
                    radius: 8,
                  ),
                  onPressed: () => setState(() {
                    _target = const Offset(220, 140);
                    _status = 'System 1 Venn · <5ms match';
                  }),
                ),
              ),
              Semantics(
                identifier: 'ino:fly-bottom-right',
                button: true,
                child: UiMixButton(
                  label: 'Fly → Bottom-Right',
                  style: const UiStyleData(
                    tone: 'subtle',
                    paddingX: 12,
                    paddingY: 6,
                    radius: 8,
                  ),
                  onPressed: () => setState(() {
                    _target = const Offset(420, 260);
                    _status = 'Spawning Live Neuron…';
                  }),
                ),
              ),
            ],
          ),
        ),
        Expanded(
          child: InoFlyingOrbOverlay(
            position: _target,
            state: state,
            orbSize: size,
            audioLevel: audioLevel,
            statusText: _status,
            onTap: () => setState(() {
              _status = 'Voice-to-Voice Active…';
            }),
            onPositionChanged: (next) => setState(() => _target = next),
          ),
        ),
      ],
    );
  }
}

final class _InoCompanionPlayground extends StatefulWidget {
  const _InoCompanionPlayground();

  @override
  State<_InoCompanionPlayground> createState() =>
      _InoCompanionPlaygroundState();
}

class _InoCompanionPlaygroundState extends State<_InoCompanionPlayground> {
  String _status = 'Drag me & tap for quick actions';
  String _badge = 'S1 · 0.6ms';

  @override
  Widget build(BuildContext context) {
    final state = context.knobs.object.dropdown<PresenceState>(
      label: 'Presence State',
      options: PresenceState.values,
      initialOption: PresenceState.listening,
      labelBuilder: (s) => s.name,
    );
    final snap = context.knobs.boolean(
      label: 'Snap To Edge',
      initialValue: true,
    );

    return InoCompanionOrb(
      state: state,
      snapToEdge: snap,
      statusLine: _status,
      routingBadge: _badge,
      actions: defaultInoCompanionActions(
        onVoice: () => setState(() {
          _badge = 'S1 · 0.5ms';
          _status = 'Listening to voice…';
        }),
        onCodebaseTree: () => setState(() {
          _badge = 'Roslyn Tree';
          _status = 'Expanded Auth slice (lazy)';
        }),
        onLiveNeuron: () => setState(() {
          _badge = 'Collectible ALC';
          _status = 'Compiled & hot-loaded neuron';
        }),
      ),
    );
  }
}

// =============================================================================
// Mix Primitives Playgrounds
// =============================================================================

final class _MixActionButtonPlayground extends StatefulWidget {
  const _MixActionButtonPlayground();

  @override
  State<_MixActionButtonPlayground> createState() =>
      _MixActionButtonPlaygroundState();
}

class _MixActionButtonPlaygroundState extends State<_MixActionButtonPlayground> {
  final List<String> _events = [];

  void _log(String entry) {
    setState(() {
      _events.insert(0, entry);
      if (_events.length > 6) _events.removeLast();
    });
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final label = context.knobs.string(
      label: 'Label',
      initialValue: 'Deploy Neuron',
    );
    final style = context.knobs.object.dropdown<UiMixButtonStyle>(
      label: 'Style',
      options: UiMixButtonStyle.values,
      initialOption: UiMixButtonStyle.primary,
      labelBuilder: (s) => s.name,
    );
    final icon = context.knobs.object.dropdown<String>(
      label: 'Icon',
      options: const ['none', 'sparkles', 'rocket', 'check', 'trash-2', 'plus'],
      initialOption: 'sparkles',
    );
    final compact = context.knobs.boolean(label: 'Compact', initialValue: false);
    final fullWidth = context.knobs.boolean(
      label: 'Full Width',
      initialValue: false,
    );
    final enabled = context.knobs.boolean(label: 'Enabled', initialValue: true);
    final tooltip = context.knobs.string(
      label: 'Tooltip / Guard',
      initialValue: 'Hover, press, or Tab-focus to inspect Mix states',
    );

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 640),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            UiMixActionButton(
              label: label,
              style: style,
              icon: icon == 'none' ? null : icon,
              compact: compact,
              fullWidth: fullWidth,
              tooltip: tooltip.isEmpty ? null : tooltip,
              onPressed: enabled
                  ? () => _log('Pressed ${style.name}("$label")')
                  : null,
            ),
            const SizedBox(height: 24),
            Text(
              'All Tones (Hover / Press / Focus)',
              style: TextStyle(
                color: c.muted,
                fontSize: 12,
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 10),
            Wrap(
              spacing: 10,
              runSpacing: 10,
              children: [
                for (final s in UiMixButtonStyle.values)
                  UiMixActionButton(
                    label: s.name[0].toUpperCase() + s.name.substring(1),
                    style: s,
                    compact: compact,
                    icon: s == UiMixButtonStyle.danger ? 'trash-2' : null,
                    onPressed: enabled
                        ? () => _log('Pressed ${s.name}')
                        : null,
                  ),
              ],
            ),
            if (_events.isNotEmpty) ...[
              const SizedBox(height: 20),
              for (final e in _events)
                Text(
                  e,
                  style: TextStyle(
                    fontFamily: 'JetBrains Mono',
                    fontSize: 12,
                    color: c.muted,
                  ),
                ),
            ],
          ],
        ),
      ),
    );
  }
}

final class _MixFieldPlayground extends StatefulWidget {
  const _MixFieldPlayground();

  @override
  State<_MixFieldPlayground> createState() => _MixFieldPlaygroundState();
}

class _MixFieldPlaygroundState extends State<_MixFieldPlayground> {
  final _controller = TextEditingController(text: 'intochat-kernel-01');
  String _lastSubmitted = '';

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final label = context.knobs.string(
      label: 'Label',
      initialValue: 'Workspace Host',
    );
    final placeholder = context.knobs.string(
      label: 'Placeholder',
      initialValue: 'Enter hostname or endpoint…',
    );
    final helper = context.knobs.string(
      label: 'Helper',
      initialValue: 'Hover or focus to inspect the Mix border & focus ring.',
    );
    final error = context.knobs.string(label: 'Error', initialValue: '');
    final leadingIcon = context.knobs.object.dropdown<String>(
      label: 'Leading Icon',
      options: const ['none', 'search', 'globe', 'lock', 'mail'],
      initialOption: 'search',
    );
    final secret = context.knobs.boolean(label: 'Secret', initialValue: false);
    final clearable = context.knobs.boolean(
      label: 'Clearable',
      initialValue: true,
    );
    final multiline = context.knobs.boolean(
      label: 'Multiline',
      initialValue: false,
    );
    final enabled = context.knobs.boolean(label: 'Enabled', initialValue: true);

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            UiMixField(
              label: label,
              controller: _controller,
              placeholder: placeholder,
              helper: helper.isEmpty ? null : helper,
              error: error.isEmpty ? null : error,
              leadingIcon: leadingIcon == 'none' ? null : leadingIcon,
              secret: secret,
              clearable: clearable,
              multiline: multiline,
              enabled: enabled,
              onChanged: (_) => setState(() {}),
              onSubmitted: (v) => setState(() => _lastSubmitted = v),
            ),
            const SizedBox(height: 14),
            Text(
              'Value: "${_controller.text}"'
              '${_lastSubmitted.isEmpty ? '' : ' · Submitted: "$_lastSubmitted"'}',
              style: TextStyle(
                fontFamily: 'JetBrains Mono',
                fontSize: 12,
                color: c.muted,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

final class _MixSelectPlayground extends StatefulWidget {
  const _MixSelectPlayground();

  @override
  State<_MixSelectPlayground> createState() => _MixSelectPlaygroundState();
}

class _MixSelectPlaygroundState extends State<_MixSelectPlayground> {
  String _selected = 'gemini-2.5-pro';

  static const _shortItems = <UiMixSelectItem<String>>[
    UiMixSelectItem(
      'gemini-2.5-pro',
      'Gemini 2.5 Pro',
      detail: 'Deep reasoning & long context',
    ),
    UiMixSelectItem(
      'gemini-2.5-flash',
      'Gemini 2.5 Flash',
      detail: 'Low-latency interactive routing',
    ),
    UiMixSelectItem(
      'local-s1-venn',
      'Local System-1 Venn',
      detail: 'Sub-5ms deterministic reflex',
    ),
    UiMixSelectItem(
      'legacy-v1',
      'Legacy Engine (Deprecated)',
      enabled: false,
      detail: 'Disabled option example',
    ),
  ];

  static const _longItems = <UiMixSelectItem<String>>[
    ..._shortItems,
    UiMixSelectItem('claude-sonnet', 'Claude Sonnet', detail: 'External MCP'),
    UiMixSelectItem('gpt-5', 'GPT-5', detail: 'External MCP'),
    UiMixSelectItem('llama-4', 'Llama 4 Local', detail: 'On-device GGUF'),
    UiMixSelectItem('qwen-3', 'Qwen 3 Coder', detail: 'On-device GGUF'),
    UiMixSelectItem('mistral-large', 'Mistral Large', detail: 'EU region'),
    UiMixSelectItem('deepseek-r1', 'DeepSeek R1', detail: 'Reasoning'),
  ];

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final label = context.knobs.string(
      label: 'Label',
      initialValue: 'Inference Model',
    );
    final hint = context.knobs.string(
      label: 'Hint',
      initialValue: 'Choose a model…',
    );
    final searchable = context.knobs.boolean(
      label: 'Searchable (>8 items)',
      initialValue: false,
    );
    final enabled = context.knobs.boolean(label: 'Enabled', initialValue: true);

    final items = searchable ? _longItems : _shortItems;
    final effectiveValue = items.any((i) => i.value == _selected)
        ? _selected
        : items.first.value;

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            UiMixSelect<String>(
              label: label,
              hint: hint,
              value: effectiveValue,
              items: items,
              onChanged: enabled
                  ? (next) => setState(() => _selected = next)
                  : null,
            ),
            const SizedBox(height: 14),
            Text(
              'Selected: $effectiveValue',
              style: TextStyle(
                fontFamily: 'JetBrains Mono',
                fontSize: 12,
                color: c.muted,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

final class _BadgePlayground extends StatelessWidget {
  const _BadgePlayground();

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final label = context.knobs.string(label: 'Label', initialValue: 'Live');
    final tone = context.knobs.object.dropdown<UiBadgeTone>(
      label: 'Tone',
      options: UiBadgeTone.values,
      initialOption: UiBadgeTone.accent,
      labelBuilder: (t) => t.name,
    );
    final dot = context.knobs.boolean(label: 'Status Dot', initialValue: true);
    final showIcon = context.knobs.boolean(
      label: 'Icon (when dot=false)',
      initialValue: false,
    );

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          UiBadge(
            label: label,
            tone: tone,
            dot: dot,
            icon: showIcon ? LucideIcons.sparkles : null,
            semanticsId: 'widgetbook:badge-primary',
          ),
          const SizedBox(height: 24),
          Text(
            'All Semantic Tones',
            style: TextStyle(
              color: c.muted,
              fontSize: 12,
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 10),
          const Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              UiBadge(label: 'Neutral', tone: UiBadgeTone.neutral),
              UiBadge(label: 'Accent', tone: UiBadgeTone.accent, dot: true),
              UiBadge(
                label: 'Synced',
                tone: UiBadgeTone.success,
                icon: LucideIcons.check,
              ),
              UiBadge(
                label: 'Degraded',
                tone: UiBadgeTone.warning,
                dot: true,
              ),
              UiBadge(
                label: 'Offline',
                tone: UiBadgeTone.danger,
                icon: LucideIcons.alertTriangle,
              ),
            ],
          ),
        ],
      ),
    );
  }
}

final class _SegmentedControlPlayground extends StatefulWidget {
  const _SegmentedControlPlayground();

  @override
  State<_SegmentedControlPlayground> createState() =>
      _SegmentedControlPlaygroundState();
}

class _SegmentedControlPlaygroundState
    extends State<_SegmentedControlPlayground> {
  String _mode = 'chat';

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final compact = context.knobs.boolean(label: 'Compact', initialValue: false);
    final expand = context.knobs.boolean(label: 'Expand', initialValue: false);
    final enabled = context.knobs.boolean(label: 'Enabled', initialValue: true);
    final showIcons = context.knobs.boolean(
      label: 'Show Icons',
      initialValue: true,
    );

    return Padding(
      padding: const EdgeInsets.all(24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 460),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: expand
              ? CrossAxisAlignment.stretch
              : CrossAxisAlignment.center,
          children: [
            UiSegmentedControl<String>(
              compact: compact,
              expand: expand,
              semanticsId: 'widgetbook:segmented-control',
              value: _mode,
              onChanged: enabled
                  ? (next) => setState(() => _mode = next)
                  : null,
              options: [
                UiSegmentedOption(
                  value: 'chat',
                  label: 'Chat',
                  icon: showIcons ? LucideIcons.messageSquare : null,
                ),
                UiSegmentedOption(
                  value: 'canvas',
                  label: 'Canvas',
                  icon: showIcons ? LucideIcons.layoutGrid : null,
                ),
                UiSegmentedOption(
                  value: 'code',
                  label: 'Code',
                  icon: showIcons ? LucideIcons.code : null,
                ),
              ],
            ),
            const SizedBox(height: 16),
            Text(
              'Active segment: $_mode',
              style: TextStyle(
                fontFamily: 'JetBrains Mono',
                fontSize: 12,
                color: c.muted,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

final class _AdaptiveSheetPlayground extends StatefulWidget {
  const _AdaptiveSheetPlayground();

  @override
  State<_AdaptiveSheetPlayground> createState() =>
      _AdaptiveSheetPlaygroundState();
}

class _AdaptiveSheetPlaygroundState extends State<_AdaptiveSheetPlayground> {
  String _lastResult = 'Tap a button to present showUiSheet';

  Future<void> _openSheet({
    required BuildContext context,
    required String title,
    required String subtitle,
    required bool showDragHandle,
    required bool sideSheet,
    required bool barrierDismissible,
  }) async {
    final result = await showUiSheet<String>(
      context: context,
      sideSheet: sideSheet,
      barrierDismissible: barrierDismissible,
      builder: (ctx) => UiSheet(
        title: title,
        subtitle: subtitle,
        showDragHandle: showDragHandle,
        semanticsId: 'widgetbook:adaptive-sheet',
        trailing: UiBadge(
          label: sideSheet ? 'Side' : 'Adaptive',
          tone: UiBadgeTone.accent,
        ),
        footer: Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            UiMixActionButton(
              label: 'Cancel',
              style: UiMixButtonStyle.ghost,
              onPressed: () => Navigator.of(ctx).pop('cancelled'),
            ),
            const SizedBox(width: 8),
            UiMixActionButton(
              label: 'Confirm',
              style: UiMixButtonStyle.primary,
              onPressed: () => Navigator.of(ctx).pop('confirmed'),
            ),
          ],
        ),
        child: const Text(
          'Switch the Widgetbook toolbar Viewport Preview to Phone (390×844) '
          'to see this automatically present as a bottom sheet, or Desktop/Tablet '
          'for a centered dialog / side sheet.',
        ),
      ),
    );
    if (mounted) {
      setState(() => _lastResult = 'Sheet closed → ${result ?? "dismissed"}');
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final title = context.knobs.string(
      label: 'Title',
      initialValue: 'Configure Neuron',
    );
    final subtitle = context.knobs.string(
      label: 'Subtitle',
      initialValue: 'Adapts to viewport breakpoint (<640px bottom sheet)',
    );
    final showDragHandle = context.knobs.boolean(
      label: 'Show Drag Handle',
      initialValue: true,
    );
    final sideSheet = context.knobs.boolean(
      label: 'Desktop Side Sheet',
      initialValue: false,
    );
    final barrierDismissible = context.knobs.boolean(
      label: 'Barrier Dismissible',
      initialValue: true,
    );

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Wrap(
            spacing: 10,
            runSpacing: 10,
            alignment: WrapAlignment.center,
            children: [
              UiMixActionButton(
                label: 'Present showUiSheet',
                icon: 'panel-right',
                onPressed: () => _openSheet(
                  context: context,
                  title: title,
                  subtitle: subtitle,
                  showDragHandle: showDragHandle,
                  sideSheet: sideSheet,
                  barrierDismissible: barrierDismissible,
                ),
              ),
              UiMixActionButton(
                label: 'Present Side Sheet',
                style: UiMixButtonStyle.secondary,
                onPressed: () => _openSheet(
                  context: context,
                  title: title,
                  subtitle: subtitle,
                  showDragHandle: showDragHandle,
                  sideSheet: true,
                  barrierDismissible: barrierDismissible,
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Text(
            _lastResult,
            style: TextStyle(
              fontFamily: 'JetBrains Mono',
              fontSize: 12,
              color: c.muted,
            ),
          ),
          const SizedBox(height: 20),
          UiSheet(
            title: title,
            subtitle: subtitle,
            showDragHandle: showDragHandle,
            trailing: const UiBadge(
              label: 'Inline Preview',
              tone: UiBadgeTone.neutral,
            ),
            child: const Text(
              'Standalone UiSheet surface preview wired to UiColors & UiTokens.',
            ),
          ),
        ],
      ),
    );
  }
}

final class _SkeletonPlayground extends StatelessWidget {
  const _SkeletonPlayground();

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final width = context.knobs.double.slider(
      label: 'Width (px)',
      min: 24,
      max: 320,
      initialValue: 180,
    );
    final height = context.knobs.double.slider(
      label: 'Height (px)',
      min: 8,
      max: 96,
      initialValue: 16,
    );
    final radius = context.knobs.double.slider(
      label: 'Radius (px)',
      min: 0,
      max: 24,
      initialValue: 8,
    );
    final circle = context.knobs.boolean(label: 'Circle', initialValue: false);

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisSize: MainAxisSize.min,
          children: [
            UiSkeleton(
              width: width,
              height: height,
              radius: radius,
              circle: circle,
              semanticsLabel: 'Loading placeholder',
            ),
            const SizedBox(height: 24),
            Text(
              'Card Skeleton Composition',
              style: TextStyle(
                color: c.muted,
                fontSize: 12,
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 10),
            Container(
              padding: const EdgeInsets.all(16),
              decoration: BoxDecoration(
                color: c.surface,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: c.hairline),
              ),
              child: const Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  UiSkeleton(height: 40, circle: true),
                  SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        UiSkeleton(width: 140, height: 14),
                        SizedBox(height: 8),
                        UiSkeleton(height: 12),
                        SizedBox(height: 6),
                        UiSkeleton(width: 200, height: 12),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

final class _AdaptiveNavBarPlayground extends StatefulWidget {
  const _AdaptiveNavBarPlayground();

  @override
  State<_AdaptiveNavBarPlayground> createState() =>
      _AdaptiveNavBarPlaygroundState();
}

class _AdaptiveNavBarPlaygroundState extends State<_AdaptiveNavBarPlayground> {
  int _selectedIndex = 0;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final breakpoint = context.knobs.double.slider(
      label: 'Compact Breakpoint (px)',
      min: 320,
      max: 1024,
      initialValue: 640,
    );
    final showBadges = context.knobs.boolean(
      label: 'Show Badges',
      initialValue: true,
    );

    final items = [
      UiNavBarItem(
        id: 'inbox',
        label: 'Inbox',
        icon: LucideIcons.inbox,
        badgeCount: showBadges ? 4 : null,
      ),
      UiNavBarItem(
        id: 'neurons',
        label: 'Neurons',
        icon: LucideIcons.cpu,
        badgeCount: showBadges ? 12 : null,
      ),
      const UiNavBarItem(
        id: 'search',
        label: 'Search',
        icon: LucideIcons.search,
      ),
      const UiNavBarItem(
        id: 'settings',
        label: 'Settings',
        icon: LucideIcons.settings,
      ),
    ];

    final viewportWidth = MediaQuery.sizeOf(context).width;
    final isCompact = viewportWidth < breakpoint;

    final navBar = UiAdaptiveNavBar(
      items: items,
      selectedIndex: _selectedIndex,
      compactBreakpoint: breakpoint,
      semanticsId: 'widgetbook:adaptive-nav-bar',
      onSelected: (i) => setState(() => _selectedIndex = i),
    );

    final content = Expanded(
      child: Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              UiBadge(
                label: isCompact
                    ? 'Bottom Bar (${viewportWidth.toInt()}px < ${breakpoint.toInt()}px)'
                    : 'Navigation Rail (${viewportWidth.toInt()}px ≥ ${breakpoint.toInt()}px)',
                tone: UiBadgeTone.accent,
                dot: true,
              ),
              const SizedBox(height: 12),
              Text(
                'Active destination: ${items[_selectedIndex].label}',
                style: TextStyle(
                  color: c.strong,
                  fontSize: 16,
                  fontWeight: FontWeight.w600,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                'Toggle Phone vs. Tablet/Desktop in the toolbar above to switch layouts live.',
                textAlign: TextAlign.center,
                style: TextStyle(color: c.muted, fontSize: 12.5),
              ),
            ],
          ),
        ),
      ),
    );

    return isCompact
        ? Column(children: [content, navBar])
        : Row(children: [navBar, content]);
  }
}
