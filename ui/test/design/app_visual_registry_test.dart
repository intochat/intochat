import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  group('Unified BrandVisuals <-> IcAppIcon asset registry parity', () {
    test('all 17 ArtCraft specs derive pngAsset from BrandVisuals with 100% parity', () {
      expect(artCraftDefaultWebPorts, hasLength(17));
      for (final studioId in artCraftDefaultWebPorts.keys) {
        final spec = IcAppIcon.builtInSpecs[studioId];
        expect(spec, isNotNull, reason: 'Missing builtInSpec for $studioId');
        expect(spec!.category, 'ArtCraft Studio');
        expect(
          spec.pngAsset,
          BrandVisuals.pngAssetPaths['artcraft:$studioId'],
          reason: 'pngAsset mismatch for $studioId',
        );
        expect(spec.pngAsset, 'assets/brands/artcraft/$studioId.png');
        expect(BrandVisuals.pngAssetForId(studioId), spec.pngAsset);
      }
    });

    test(
      'brand apps and bitcoin dynamic apps derive assets from BrandVisuals',
      () {
        final aspire = IcAppIcon.builtInSpecs['aspire']!;
        expect(aspire.pngAsset, BrandVisuals.pngAssetPaths['brand:aspire']);
        expect(aspire.svgAsset, BrandVisuals.svgAssetPaths['brand:aspire']);

        final muncly = IcAppIcon.builtInSpecs['muncly']!;
        expect(muncly.svgAsset, BrandVisuals.svgAssetPaths['brand:muncly']);

        final btc = IcAppIcon.resolveSpec('btcalert');
        expect(btc.svgAsset, BrandVisuals.svgAssetPaths['brand:bitcoin']);
      },
    );

    test('BrandVisuals.pngAssetForId and svgAssetForId cover all NeuronIcon brand/provider keys', () {
      expect(BrandVisuals.pngAssetForId('aspire'), 'assets/brands/aspire.png');
      expect(
        BrandVisuals.pngAssetForId('photocraft'),
        'assets/brands/artcraft/photocraft.png',
      );
      expect(
        BrandVisuals.svgAssetForId('anthropic'),
        'assets/brands/providers/anthropic.svg',
      );
      expect(BrandVisuals.svgAssetForId('github'), 'assets/brands/github.svg');
      expect(BrandVisuals.pngAssetForId('unknown-app'), isNull);
      expect(BrandVisuals.svgAssetForId('unknown-app'), isNull);
    });
  });

  group('Dedicated ArtCraft web URL & port helper', () {
    test('isArtCraftApp recognizes all 17 studios, artcraft: prefix, and videocraft alias', () {
      for (final studioId in artCraftDefaultWebPorts.keys) {
        expect(isArtCraftApp(studioId), isTrue);
        expect(isArtCraftApp('artcraft:$studioId'), isTrue);
        expect(isArtCraftApp(studioId.toUpperCase()), isTrue);
      }
      expect(isArtCraftApp('videocraft'), isTrue);
      expect(isArtCraftApp('ino'), isFalse);
      expect(isArtCraftApp(''), isFalse);
      expect(isArtCraftApp(null), isFalse);
    });

    test('findArtCraftByUrlOrName resolves studio names, default ports, and special ports 18110/18111', () {
      expect(findArtCraftByUrlOrName('http://localhost:18110/'), 'photocraft');
      expect(findArtCraftByUrlOrName('http://127.0.0.1:18111/'), 'filmcraft');
      expect(findArtCraftByUrlOrName('videocraft studio'), 'filmcraft');
      expect(
        findArtCraftByUrlOrName('/artcraft/vectorcraft/index.html'),
        'vectorcraft',
      );
      expect(
        findArtCraftByUrlOrName('/artcraft/artcraft/index.html'),
        'artcraft',
      );

      for (final entry in artCraftDefaultWebPorts.entries) {
        expect(
          findArtCraftByUrlOrName('http://localhost:${entry.value}'),
          entry.key,
          reason: 'Expected port ${entry.value} to map to ${entry.key}',
        );
      }

      expect(findArtCraftByUrlOrName('https://example.com'), isNull);
      expect(findArtCraftByUrlOrName(null), isNull);
    });

    test('resolveArtCraftWebUrl rewrites localhost and relative /artcraft/ URLs on Web and preserves doc query', () {
      final base = Uri.parse('https://app.intochat.test/shell');

      expect(
        resolveArtCraftWebUrl(
          'http://localhost:18110/?doc=hero%20banner.psd',
          isWeb: true,
          baseUri: base,
        ),
        'https://app.intochat.test/artcraft/photocraft/index.html?doc=hero+banner.psd',
      );

      expect(
        resolveArtCraftWebUrl(
          'http://127.0.0.1:18111/',
          isWeb: true,
          baseUri: base,
          uiBaseOrigin: 'https://kernel.intochat.test:8443/ui',
        ),
        'https://kernel.intochat.test:8443/artcraft/filmcraft/index.html',
      );

      expect(
        resolveArtCraftWebUrl(
          '/artcraft/solidcraft/index.html',
          isWeb: true,
          baseUri: base,
        ),
        'https://app.intochat.test/artcraft/solidcraft/index.html',
      );

      // Native desktop (isWeb: false) keeps raw URL unchanged.
      expect(
        resolveArtCraftWebUrl(
          'http://localhost:18082/',
          isWeb: false,
          baseUri: base,
        ),
        'http://localhost:18082/',
      );

      // Non-local external URLs are not rewritten.
      expect(
        resolveArtCraftWebUrl(
          'https://external.example/photocraft',
          isWeb: true,
          baseUri: base,
        ),
        'https://external.example/photocraft',
      );
    });
  });

  group('Unified SmartAppTileSpec factories', () {
    test(
      'fromNode, fromProjectionTile, and fromIconSpec produce identical specs',
      () {
        const node = AppTileNode(
          key: 'tile-vectorcraft',
          appId: 'vectorcraft',
          title: 'VectorCraft',
          icon: 'pen-tool',
          category: 'ArtCraft Studio',
          launch: AppLaunch.maximized,
          dock: 3,
          appKind: 'compiled',
          description: 'Vector editor',
          command: null,
          automationName: '',
        );
        const tile = DesktopAppTile(
          id: 'vectorcraft',
          title: 'VectorCraft',
          icon: 'pen-tool',
          category: 'ArtCraft Studio',
          launch: AppLaunch.maximized,
          dock: 3,
          kind: 'compiled',
          description: 'Vector editor',
        );

        final fromNode = SmartAppTileSpec.fromNode(node, brain: 'brain-9');
        final fromTile = SmartAppTileSpec.fromProjectionTile(
          tile,
          brain: 'brain-9',
        );
        final fromIcon = SmartAppTileSpec.fromIconSpec(
          'vectorcraft',
          titleOverride: 'VectorCraft',
          categoryOverride: 'ArtCraft Studio',
          launch: AppLaunch.maximized,
          dock: 3,
          appKind: 'compiled',
          description: 'Vector editor',
          iconSlug: 'pen-tool',
          brain: 'brain-9',
        );

        for (final candidate in [fromTile, fromIcon]) {
          expect(candidate.id, fromNode.id);
          expect(candidate.name, fromNode.name);
          expect(candidate.icon, fromNode.icon);
          expect(candidate.color, fromNode.color);
          expect(candidate.accentHex, fromNode.accentHex);
          expect(candidate.category, fromNode.category);
          expect(candidate.neuronUri, fromNode.neuronUri);
          expect(candidate.launch, fromNode.launch);
          expect(candidate.dock, fromNode.dock);
          expect(candidate.appKind, fromNode.appKind);
          expect(candidate.description, fromNode.description);
          expect(candidate.iconSlug, fromNode.iconSlug);
        }
        expect(fromNode.neuronUri, 'neuron://brain-9/apps/vectorcraft');
      },
    );

    test('unit appKind resolves to neuron://<brain>/units/<id> and blank brain defaults to local', () {
      const unitTile = DesktopAppTile(
        id: 'helloworld',
        title: '',
        kind: 'unit',
      );
      final spec = SmartAppTileSpec.fromProjectionTile(unitTile, brain: '   ');
      expect(spec.name, 'Hello World');
      expect(spec.category, 'Micro-Apps');
      expect(spec.neuronUri, 'neuron://local/units/helloworld');
    });
  });
}
