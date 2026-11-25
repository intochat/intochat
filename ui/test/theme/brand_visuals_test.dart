import 'package:flutter/material.dart';
import 'package:flutter_svg/flutter_svg.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/desktop/command_palette.dart';
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/desktop/smart_desktop.dart';
import 'package:intochat_os_flutter_shell/desktop/window_frame.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  group('BrandVisuals allowlisted catalog & C#-driven node binding', () {
    test('resolves all 8 provider SVG assets, accent colors from wire hex, and labels from C# nodes', () {
      final sonnet = BrandVisuals.fromNode(
        label: 'Sonnet 4.6',
        iconKey: 'provider:anthropic',
        accentHex: '#D97757',
      );
      expect(sonnet.label, 'Sonnet 4.6');
      expect(sonnet.svgAsset, 'assets/brands/providers/anthropic.svg');
      expect(sonnet.accent, const Color(0xFFD97757));

      final gpt = BrandVisuals.fromNode(
        label: 'GPT-5.4',
        iconKey: 'provider:openai',
      );
      expect(gpt.label, 'GPT-5.4');
      expect(gpt.svgAsset, 'assets/brands/providers/openai.svg');
      expect(gpt.monochromeSvg, isTrue);

      final gemini = BrandVisuals.fromNode(
        label: 'Gemini 3.6 Pro',
        iconKey: 'provider:google',
      );
      expect(gemini.label, 'Gemini 3.6 Pro');
      expect(gemini.svgAsset, 'assets/brands/providers/google.svg');

      for (final providerKey in const [
        'provider:ollama',
        'provider:qwen',
        'provider:xai',
        'provider:openrouter',
        'provider:foundry',
        'provider:foundrylocal',
        'provider:openaicompatible',
        'provider:sherpaonnx',
      ]) {
        final spec = BrandVisuals.tryForIconKey(
          providerKey,
          label: providerKey,
        );
        expect(spec, isNotNull, reason: 'Expected asset for $providerKey');
        expect(spec!.svgAsset, startsWith('assets/brands/providers/'));
        expect(
          NeuronIconKind.fromKey(providerKey),
          isNot(NeuronIconKind.generic),
          reason: 'Expected NeuronIconKind for $providerKey',
        );
      }
    });

    test('resolves Aspire PNG, GitHub SVG, all 17 ArtCraft PNGs, and never loads arbitrary external URLs', () {
      final aspire = BrandVisuals.fromNode(
        label: 'Aspire',
        iconKey: 'brand:aspire',
        accentHex: '#512BD4',
      );
      expect(aspire.label, 'Aspire');
      expect(aspire.pngAsset, 'assets/brands/aspire.png');
      expect(aspire.accent, const Color(0xFF512BD4));

      final github = BrandVisuals.fromNode(
        label: 'GitHub',
        iconKey: 'brand:github',
      );
      expect(github.label, 'GitHub');
      expect(github.svgAsset, 'assets/brands/github.svg');

      const studioIds = <String>[
        'artcraft',
        'photocraft',
        'filmcraft',
        'vectorcraft',
        'lightcraft',
        'effectcraft',
        'designcraft',
        'pdfcraft',
        'wordcraft',
        'soundcraft',
        'deckcraft',
        'cadcraft',
        'gridcraft',
        'figcraft',
        'texturecraft',
        'materialcraft',
        'solidcraft',
      ];
      expect(studioIds, hasLength(17));
      for (final id in studioIds) {
        final spec = BrandVisuals.tryForIconKey('artcraft:$id', label: id);
        expect(spec, isNotNull);
        expect(spec!.pngAsset, 'assets/brands/artcraft/$id.png');
      }

      final untrusted = BrandVisuals.fromNode(
        label: 'External',
        iconKey: 'https://evil.example/logo.svg',
      );
      expect(untrusted.pngAsset, isNull);
      expect(untrusted.svgAsset, isNull);
    });
  });

  testWidgets(
    'ConversationView renders Sonnet 4.6 with Anthropic icon, Aspire ActivityNode with aspire.png & label Aspire, and ApprovalCard with GitHub icon',
    (tester) async {
      final root = <String, dynamic>{
        'kind': 'conversation',
        'key': 'conv',
        'status': 'ready',
        'composer': {
          'key': 'composer',
          'onSubmit': {'target': '.', 'signal': 'send'},
          'placeholder': 'Ask Ino…',
          'enabled': true,
          'activeModelLabel': 'Sonnet 4.6',
          'activeProviderIcon': 'provider:anthropic',
        },
        'messages': [
          {
            'id': 'm-assistant',
            'role': 'assistant',
            'text': 'Checked your .NET Aspire cluster and staged the PR.',
            'status': 'completed',
            'model': 'claude-sonnet-4-6',
            'modelLabel': 'Sonnet 4.6',
            'providerIcon': 'provider:anthropic',
            'activities': [
              {
                'id': 'act-aspire',
                'title': 'Aspire',
                'status': 'done',
                'detail': 'Resources',
                'icon': 'brand:aspire',
                'category': 'Developer',
              },
              {
                'id': 'act-photo',
                'title': 'PhotoCraft',
                'status': 'done',
                'detail': 'Export hero banner',
                'icon': 'artcraft:photocraft',
                'category': 'ArtCraft Studio',
              },
            ],
            'cards': [
              {
                'kind': 'approvalCard',
                'key': 'approve-gh',
                'id': 'plan-gh',
                'title': 'Create Pull Request #42',
                'summary': 'Merge impl/visuals into main',
                'systemName': 'IGitHub',
                'icon': 'brand:github',
                'capabilityLabel': 'GitHub',
                'rows': const <Object>[],
                'status': 'pending',
                'resolve': {'planId': 'plan-gh'},
              },
            ],
          },
        ],
      };

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: ViewHost(
              knowsApp: (_) => false,
              kind: 'view',
              name: 'brain-1/apps/ino/view',
              load: (_, _) async => {
                'generation': 1,
                'view': {'title': 'Ino', 'root': root},
              },
              watch: (_, _) => const Stream.empty(),
              onAction: (_) async => null,
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      // 1. Assistant message chip & composer active model chip render 'Sonnet 4.6' + Anthropic SVG
      expect(
        find.byKey(const ValueKey('msg-model:m-assistant')),
        findsOneWidget,
      );
      expect(
        find.byKey(const ValueKey('composer-active-model')),
        findsOneWidget,
      );
      expect(find.text('Sonnet 4.6'), findsNWidgets(2));

      final svgs = tester.widgetList<SvgPicture>(find.byType(SvgPicture));
      expect(
        svgs.any(
          (s) => s.bytesLoader.toString().contains(
            'assets/brands/providers/anthropic.svg',
          ),
        ),
        isTrue,
      );

      // 2. IAspire ActivityNode renders with aspire.png & label 'Aspire'
      expect(find.text('Aspire'), findsOneWidget);
      expect(find.text('· Resources'), findsOneWidget);

      final images = tester.widgetList<Image>(find.byType(Image)).toList();
      expect(
        images.any(
          (img) =>
              img.image is AssetImage &&
              (img.image as AssetImage).assetName == 'assets/brands/aspire.png',
        ),
        isTrue,
      );
      expect(
        images.any(
          (img) =>
              img.image is AssetImage &&
              (img.image as AssetImage).assetName ==
                  'assets/brands/artcraft/photocraft.png',
        ),
        isTrue,
      );

      // 3. ApprovalCard renders GitHub brand source label & SVG
      expect(find.text('GitHub'), findsOneWidget);
      expect(
        svgs.any(
          (s) => s.bytesLoader.toString().contains('assets/brands/github.svg'),
        ),
        isTrue,
      );
    },
  );

  testWidgets(
    'AllAppsLauncherOverlay renders PhotoCraft with photocraft.png and all 17 ArtCraft studio PNGs including solidcraft.png',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      const studioIds = [
        'photocraft',
        'vectorcraft',
        'filmcraft',
        'motioncraft',
        'sculptcraft',
        'soundcraft',
        'paintcraft',
        'typecraft',
        'colorcraft',
        'meshcraft',
        'shadercraft',
        'cadcraft',
        'gridcraft',
        'figcraft',
        'texturecraft',
        'materialcraft',
        'solidcraft',
      ];

      String? launched;
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: AllAppsLauncherOverlay(
              apps: [
                for (final id in studioIds) SmartAppTileSpec.fromIconSpec(id),
              ],
              onLaunchApp: (id) => launched = id,
              onClose: () {},
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      final photoTile = find.byKey(const ValueKey('all-apps-tile-photocraft'));
      expect(photoTile, findsOneWidget);

      final photoImages = tester
          .widgetList<Image>(
            find.descendant(of: photoTile, matching: find.byType(Image)),
          )
          .toList();
      expect(photoImages, isNotEmpty);
      expect(
        photoImages.any(
          (img) =>
              img.image is AssetImage &&
              (img.image as AssetImage).assetName ==
                  'assets/brands/artcraft/photocraft.png',
        ),
        isTrue,
      );

      // Filter to ArtCraft Studio so all 17 studio tiles are visible in the viewport
      await tester.tap(
        find.byKey(const ValueKey('all-apps-cat-ArtCraft Studio')),
      );
      await tester.pumpAndSettle();

      final solidTile = find.byKey(const ValueKey('all-apps-tile-solidcraft'));
      expect(solidTile, findsOneWidget);
      final solidImages = tester
          .widgetList<Image>(
            find.descendant(of: solidTile, matching: find.byType(Image)),
          )
          .toList();
      expect(
        solidImages.any(
          (img) =>
              img.image is AssetImage &&
              (img.image as AssetImage).assetName ==
                  'assets/brands/artcraft/solidcraft.png',
        ),
        isTrue,
      );

      await tester.tap(photoTile);
      await tester.pumpAndSettle();
      expect(launched, 'photocraft');
    },
  );

  testWidgets(
    'Dynamic runtime micro-app helloworld renders in AllAppsLauncherOverlay, Dock, CommandPalette, and opens in LiveDesktopShell',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final activations = <Map<String, dynamic>>[];
      List<SmartAppTileSpec> dynamicExtras = const [];

      Map<String, dynamic> desktopWithHelloWorld() => {
        'generation': 1,
        'view': {
          'title': 'Desktop',
          'root': {
            'kind': 'column',
            'gap': 12,
            'padding': 0,
            'extents': [0, 68],
            'children': [
              {'kind': 'column', 'gap': 12, 'padding': 0, 'children': const []},
              {
                'kind': 'row',
                'gap': 4,
                'padding': 0,
                'children': [
                  {
                    'kind': 'button',
                    'key': 'dock-ino',
                    'label': 'Ino',
                    'command': {'target': '.', 'signal': 'open', 'args': 'ino'},
                    'enabled': true,
                  },
                  {
                    'kind': 'button',
                    'key': 'dock-helloworld',
                    'label': 'Hello World',
                    'command': {
                      'target': '.',
                      'signal': 'open',
                      'args': 'helloworld',
                    },
                    'enabled': true,
                  },
                ],
              },
            ],
          },
        },
      };

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              // Caller initially only knew 'ino', yet 'helloworld' is dynamically published on the desktop!
              knowsApp: (app) => app == 'ino',
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              onDynamicAppsChanged: (extras) => dynamicExtras = extras,
              load: (kind, name) async {
                if (name == 'brain-1/apps/desktop/view') {
                  return desktopWithHelloWorld();
                }
                if (name == 'brain-1/apps/helloworld/view') {
                  return {
                    'generation': 1,
                    'view': {
                      'title': 'Hello World',
                      'root': {
                        'kind': 'text',
                        'value': 'Hello from live runtime grain!',
                      },
                    },
                  };
                }
                return {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': 'Viewport: $name'},
                  },
                };
              },
              watch: (_, _) => const Stream.empty(),
              onAction: (_) async => null,
              onActivate: activations.add,
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      // 1. Dynamic app 'helloworld' is registered in dynamicExtras (not in the active-only dock while closed)
      expect(find.byKey(const ValueKey('dock-item-helloworld')), findsNothing);
      expect(dynamicExtras.any((s) => s.id == 'helloworld'), isTrue);

      // 2. Open All Apps Launcher and verify 'helloworld' tile is listed under 'All' & 'Micro-Apps'
      await tester.tap(find.byKey(const ValueKey('os-bar-all-apps')));
      await tester.pump();

      expect(
        find.byKey(const ValueKey('all-apps-launcher-overlay')),
        findsOneWidget,
      );
      await tester.tap(find.byKey(const ValueKey('all-apps-cat-Micro-Apps')));
      await tester.pump();

      final helloTile = find.byKey(const ValueKey('all-apps-tile-helloworld'));
      expect(helloTile, findsOneWidget);
      expect(find.text('Hello World'), findsWidgets);

      // 3. Click 'Hello World' tile in All Apps Launcher -> opens WindowFrame with live viewport and appears in active dock!
      await tester.tap(helloTile);
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(WindowFrame), findsOneWidget);
      expect(find.text('Hello from live runtime grain!'), findsOneWidget);
      expect(
        find.byKey(const ValueKey('dock-item-helloworld')),
        findsOneWidget,
      );
      expect(
        activations.any(
          (a) => a['application'] == 'helloworld' && a['operation'] == 'open',
        ),
        isTrue,
      );

      // 4. Verify CommandPalette also lists and launches dynamic 'helloworld'
      String? paletteLaunched;
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: CommandPalette(
              apps: dynamicExtras,
              onClose: () {},
              onLaunchApp: (id) => paletteLaunched = id,
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.enterText(
        find.byKey(const ValueKey('command-palette-input')),
        'Hello World',
      );
      await tester.pump();
      expect(find.text('Open Hello World'), findsOneWidget);
      await tester.tap(find.text('Open Hello World'));
      await tester.pump();
      expect(paletteLaunched, 'helloworld');
    },
  );

  testWidgets(
    'WindowFrame titlebar renders app icon + Split Left / Split Right workspace snap, and NeuronIcon resolves brand keys',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final state = DesktopWindowState(
        id: 7,
        appId: 'aspire',
        title: 'Aspire Dashboard',
        neuronUri: 'neuron://brain-1/apps/aspire',
        bounds: const Rect.fromLTWH(200, 120, 760, 520),
      );

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: Stack(
              children: [
                WindowFrame(
                  window: state,
                  focused: true,
                  forceZoom: false,
                  desk: const Size(1440, 840),
                  child: Column(
                    children: [
                      NeuronIcon.fromBrandKey('provider:anthropic', size: 24),
                      NeuronIcon.fromBrandKey('artcraft:photocraft', size: 24),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      // Titlebar shows aspire.png via IcAppIcon
      final images = tester.widgetList<Image>(find.byType(Image)).toList();
      expect(
        images.any(
          (img) =>
              img.image is AssetImage &&
              (img.image as AssetImage).assetName == 'assets/brands/aspire.png',
        ),
        isTrue,
      );

      // Snap left half -> snaps cleanly to left half of 1440x840 desk
      await tester.tap(find.byKey(const ValueKey('window-snap-left-btn-7')));
      await tester.pump();
      expect(state.bounds.left, 12);
      expect(state.bounds.width, closeTo((1440 - 36) / 2, 0.5));

      // Snap right half -> snaps cleanly to right half of 1440x840 desk
      await tester.tap(find.byKey(const ValueKey('window-snap-right-btn-7')));
      await tester.pump();
      expect(state.bounds.left, greaterThan(700));
      expect(state.bounds.width, closeTo((1440 - 36) / 2, 0.5));
    },
  );
}
