import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
// ignore: depend_on_referenced_packages
import 'package:widgetbook/widgetbook.dart';

import 'src/ui/ino/ino_companion_orb.dart';
import 'src/ui/ino/ino_orb_v2.dart';
import 'src/ui/mix/ui_mix_style.dart';
import 'src/ui/ui_control_surface.dart';

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

final class IntoChatWidgetbookApp extends StatelessWidget {
  const IntoChatWidgetbookApp({super.key, required this.catalog});

  final List<Map<String, dynamic>> catalog;

  @override
  Widget build(BuildContext context) {
    final catalogComponents = <WidgetbookComponent>[
      for (final entry in catalog)
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
      addons: [
        MaterialThemeAddon(
          themes: [
            WidgetbookTheme(name: 'Dark', data: UiTheme.dark(null)),
            WidgetbookTheme(name: 'Light', data: UiTheme.light(null)),
          ],
        ),
        TextScaleAddon(min: 0.85, max: 1.5),
        AlignmentAddon(initialAlignment: Alignment.center),
      ],
      directories: [
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
