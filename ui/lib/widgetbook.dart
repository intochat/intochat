import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
// ignore: depend_on_referenced_packages
import 'package:widgetbook/widgetbook.dart';

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
    final enabled = context.knobs.boolean(
      label: 'Enabled',
      initialValue: true,
    );

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
                  'Hover / Press / Focus Me (${state.isPressed ? "Pressed" : state.isHovered ? "Hovered" : "Idle"})',
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
