import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

void main() {
  group('DesktopShellProjection wire alignment', () {
    test('parses C# System.Text.Json Web camelCase JSON including integer AppLaunch enum', () {
      // Exact payload shape emitted by AppUiEndpoints.ReadDesktopProjection (JsonSerializerDefaults.Web)
      final payload = <String, dynamic>{
        'windows': [
          {
            'id': 'w-3',
            'appId': 'creator',
            'title': 'Creator',
            'viewportUri': 'owner/apps/desktop/window/w-3',
            'tabs': [
              {
                'id': 'tab-1',
                'appId': 'calculator',
                'title': 'Calculator',
                'selected': false,
              },
              {
                'id': 'tab-2',
                'appId': 'creator',
                'title': 'Creator',
                'selected': true,
              },
            ],
            'activeTabId': 'tab-2',
            'isPinned': true,
            'isFavourite': true,
          },
        ],
        'focusedViewId': 'tab-2',
        'pins': ['calculator', 'creator'],
        'tiles': [
          {
            'id': 'calculator',
            'title': 'Calculator',
            'icon': 'calculator',
            'category': 'Tools',
            'dock': 4,
            'launch': 0, // AppLaunch.Windowed
            'kind': 'compiled',
            'description': 'Quick math',
          },
          {
            'id': 'photocraft',
            'title': 'PhotoCraft',
            'icon': 'artcraft:photocraft',
            'category': 'ArtCraft Studio',
            'dock': 5,
            'launch': 1, // AppLaunch.Maximized
            'kind': 'compiled',
          },
          {
            'id': 'helloworld',
            'title': 'HelloWorld',
            'icon': 'zap',
            'category': 'Micro-App',
            'launch': 'maximized',
            'kind': 'unit',
          },
        ],
        'openWidgets': ['launcher', 'signal', 'clock'],
        'stylePreset': 'liquid-glass',
        'barLayout': 'bottom',
        'layoutMode': 'spatial-grid',
        'isNarrow': true,
      };

      final projection = DesktopShellProjection.fromJson(payload);

      expect(projection.focusedViewId, 'tab-2');
      expect(projection.pins, ['calculator', 'creator']);
      expect(projection.openWidgets, ['launcher', 'signal', 'clock']);
      expect(projection.stylePreset, 'liquid-glass');
      expect(projection.barLayout, 'bottom');
      expect(projection.layoutMode, 'spatial-grid');
      expect(projection.isNarrow, isTrue);

      expect(projection.windows, hasLength(1));
      final win = projection.windows.single;
      expect(win.id, 'w-3');
      expect(win.numericId, 3);
      expect(win.appId, 'creator');
      expect(win.title, 'Creator');
      expect(win.viewportUri, 'owner/apps/desktop/window/w-3');
      expect(win.activeTabId, 'tab-2');
      expect(win.activeTabIndex, 1);
      expect(win.isPinned, isTrue);
      expect(win.isFavourite, isTrue);
      expect(win.tabs, hasLength(2));
      expect(win.tabs[0].selected, isFalse);
      expect(win.tabs[1].selected, isTrue);

      expect(projection.tiles, hasLength(3));
      expect(projection.tiles[0].launch, AppLaunch.windowed);
      expect(projection.tiles[1].launch, AppLaunch.maximized);
      expect(projection.tiles[2].launch, AppLaunch.maximized);
      expect(projection.tiles[2].kind, 'unit');

      // Round-trip through toJson -> fromJson preserves every field.
      final roundTrip = DesktopShellProjection.fromJson(projection.toJson());
      expect(roundTrip.focusedViewId, projection.focusedViewId);
      expect(roundTrip.openWidgets, projection.openWidgets);
      expect(roundTrip.windows.single.viewportUri, win.viewportUri);
      expect(roundTrip.tiles[1].launch, AppLaunch.maximized);
    });
  });
}
