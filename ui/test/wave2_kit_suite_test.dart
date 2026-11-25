import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Future<void> pumpSurface(
    WidgetTester tester,
    Widget child, {
    required Brightness brightness,
    double width = 1280,
    double height = 800,
    bool disableAnimations = false,
  }) async {
    tester.view.physicalSize = Size(width, height);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(() {
      tester.view.resetPhysicalSize();
      tester.view.resetDevicePixelRatio();
    });
    await tester.pumpWidget(
      MediaQuery(
        data: MediaQueryData(
          size: Size(width, height),
          disableAnimations: disableAnimations,
        ),
        child: MaterialApp(
          key: ValueKey('app_${brightness.name}_${width.toInt()}'),
          theme: UiTheme.intochat(brightness),
          home: Scaffold(
            body: SizedBox(width: width, height: height, child: child),
          ),
        ),
      ),
    );
    await tester.pump();
  }

  group('UK-6 Icons & Brand Visuals (Light & Dark)', () {
    test('uiIcon and shellIcon resolve all Kit.Icons & catalogue names', () {
      const requiredIcons = [
        'hand',
        'loader-circle',
        'triangle-alert',
        'check',
        'sparkles',
        'landmark',
        'panel-left',
        'search',
        'sliders',
        'sliders-horizontal',
        'folder',
        'folder-open',
        'file-text',
        'bar-chart-2',
        'line-chart',
        'area-chart',
        'pie-chart',
        'git-branch',
        'refresh-cw',
        'eye',
        'eye-off',
        'x',
        'lock',
        'arrow-up',
        'arrow-down',
        'arrow-left',
        'arrow-right',
        'chevron-right',
        'chevron-down',
        'chevrons-up-down',
        'calculator',
        'hard-drive',
        'upload',
        'save',
        'wand-sparkles',
        'clapperboard',
        'aperture',
        'pen-tool',
        'compass',
        'message-square',
        'history',
        'coins',
        'wallet',
        // Case / camelCase / underscore normalization
        'loaderCircle',
        'bar_chart_2',
        'RefreshCw',
      ];
      for (final name in requiredIcons) {
        expect(uiIcon(name), isNotNull, reason: 'uiIcon("$name") was null');
        expect(
          shellIcon(name),
          isNotNull,
          reason: 'shellIcon("$name") was null',
        );
      }
    });

    for (final brightness in Brightness.values) {
      testWidgets(
        'BrandVisuals & IcAppIcon render providers, brands & OS apps in ${brightness.name} mode',
        (tester) async {
          const canonicalBrandAndAppKeys = [
            'provider:anthropic',
            'provider:openai',
            'brand:aspire',
            'provider:ollama',
            'provider:google',
            'provider:qwen',
            'provider:openrouter',
            'provider:foundry',
            'brand:github',
            'app:ino',
            'app:settings',
            'app:files',
            'app:gallery',
            'app:creator',
            'app:desktop',
            'app:workspace',
            'app:tasks',
            'app:calculator',
            'app:data',
            'app:usage',
            'app:friends',
            'app:markdraw',
            'artcraft:artcraft',
            'artcraft:photocraft',
            'artcraft:filmcraft',
            'app:brain',
          ];
          const appIds = [
            'ino',
            'settings',
            'files',
            'gallery',
            'creator',
            'desktop',
            'workspace',
            'tasks',
            'calculator',
            'data',
            'usage',
            'friends',
            'markdraw',
            'artcraft',
            'photocraft',
            'filmcraft',
            'brain',
          ];

          final specs = <BrandVisualSpec>[
            for (final k in canonicalBrandAndAppKeys)
              BrandVisuals.tryForIconKey(k, label: k)!,
          ];
          expect(specs, hasLength(canonicalBrandAndAppKeys.length));

          await pumpSurface(
            tester,
            SingleChildScrollView(
              child: Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  for (final spec in specs) IcBrandChip(spec: spec),
                  for (final app in appIds) IcAppIcon(appId: app, size: 32),
                ],
              ),
            ),
            brightness: brightness,
          );
          expect(tester.takeException(), isNull);
          expect(find.byType(IcBrandChip), findsNWidgets(specs.length));
          expect(find.byType(IcAppIcon), findsNWidgets(appIds.length));
        },
      );
    }
  });

  group('UK-7 & UK-8 Responsive SplitPane, Grid & Focus', () {
    for (final brightness in Brightness.values) {
      testWidgets(
        'SplitPane renders side-by-side at 1280px with keyboard sash nudge and collapses cleanly at 480px (${brightness.name})',
        (tester) async {
          const splitNode = SplitPaneNode(
            leadingExtent: 260,
            gap: 12,
            leading: ColumnNode(
              gap: 6,
              padding: 0,
              extents: null,
              children: [
                NavItemNode(
                  key: 'nav_overview',
                  label: 'Overview',
                  icon: 'house',
                  action: 'overview',
                  selected: true,
                  badge: '3',
                  command: null,
                  automationName: 'Overview',
                ),
                NavItemNode(
                  key: 'nav_vault',
                  label: 'Vault',
                  icon: 'landmark',
                  action: 'vault',
                  selected: false,
                  badge: null,
                  command: null,
                  automationName: 'Vault',
                ),
              ],
            ),
            trailing: HeadingNode(
              value: 'Main Workspace Content',
              level: 1,
              subtitle: 'Responsive body',
            ),
          );

          String? activatedKey;
          final ctx = NodeContext(
            view: 'local/apps/files/view',
            send: (event) async {
              activatedKey = event['key'] as String?;
              return null;
            },
          );

          // Expanded 1280px: side-by-side panes + sash visible, no compact drawer toggle.
          await pumpSurface(
            tester,
            NodeRenderer.render(splitNode, ctx),
            brightness: brightness,
            width: 1280,
          );
          expect(tester.takeException(), isNull);
          expect(find.text('Overview'), findsOneWidget);
          expect(find.text('Main Workspace Content'), findsOneWidget);
          expect(
            find.byKey(const ValueKey('split_pane_drawer_toggle')),
            findsNothing,
          );

          // Keyboard activation on NavItemNode draws focus ring & dispatches key.
          final vaultFinder = find.byKey(const ValueKey('nav:nav_vault'));
          final focusable = tester.widget<FocusableActionDetector>(
            find.descendant(
              of: vaultFinder,
              matching: find.byType(FocusableActionDetector),
            ),
          );
          expect(focusable.enabled, isTrue);
          await tester.tap(vaultFinder);
          await tester.pump();
          expect(activatedKey, 'nav_vault');

          // Compact 480px: collapses leading sidebar into toggleable drawer without overflow!
          await pumpSurface(
            tester,
            NodeRenderer.render(splitNode, ctx),
            brightness: brightness,
            width: 480,
            disableAnimations: true,
          );
          expect(tester.takeException(), isNull);
          expect(
            find.byKey(const ValueKey('split_pane_drawer_toggle')),
            findsOneWidget,
          );
          expect(find.text('Overview'), findsNothing);
          expect(find.text('Main Workspace Content'), findsOneWidget);

          // Open compact navigation drawer:
          await tester.tap(
            find.byKey(const ValueKey('split_pane_drawer_toggle')),
          );
          await tester.pump();
          expect(
            find.byKey(const ValueKey('split_pane_compact_drawer')),
            findsOneWidget,
          );
          expect(find.text('Overview'), findsOneWidget);
        },
      );

      testWidgets(
        'Grid collapses 4-column layout to 2 columns at 480px and 1 column at 360px (${brightness.name})',
        (tester) async {
          final gridNode = GridNode(
            columns: 4,
            gap: 12,
            children: [
              for (var i = 0; i < 4; i++)
                StatCardNode(
                  key: 'stat_$i',
                  label: 'Metric $i',
                  value: '${(i + 1) * 120}',
                  delta: '+4%',
                  tone: BadgeTone.done,
                  icon: 'bar-chart-2',
                  automationName: 'Metric $i',
                ),
            ],
          );
          const ctx = NodeContext(view: 'local/apps/usage/view');

          // 1280px -> 4 columns across one row
          await pumpSurface(
            tester,
            NodeRenderer.render(gridNode, ctx),
            brightness: brightness,
            width: 1280,
          );
          expect(tester.takeException(), isNull);
          final y0Expanded = tester.getTopLeft(find.text('Metric 0')).dy;
          final y2Expanded = tester.getTopLeft(find.text('Metric 2')).dy;
          expect(y0Expanded, closeTo(y2Expanded, 1.0));

          // 480px -> collapses to 2 columns (Metric 2 wraps to second row)
          await pumpSurface(
            tester,
            NodeRenderer.render(gridNode, ctx),
            brightness: brightness,
            width: 480,
          );
          expect(tester.takeException(), isNull);
          final y0Compact = tester.getTopLeft(find.text('Metric 0')).dy;
          final y1Compact = tester.getTopLeft(find.text('Metric 1')).dy;
          final y2Compact = tester.getTopLeft(find.text('Metric 2')).dy;
          expect(y0Compact, closeTo(y1Compact, 1.0));
          expect(y2Compact, greaterThan(y0Compact + 20));
        },
      );
    }
  });

  group('UK-10 & UK-11 Interactive Chart & Sortable/Filterable DataTable', () {
    for (final brightness in Brightness.values) {
      testWidgets(
        'Chart supports columns, line, area, pie + hover readout tooltip & point click (${brightness.name})',
        (tester) async {
          for (final chartKind in ['columns', 'line', 'area', 'pie']) {
            String? clickedPoint;
            final node = ChartNode(
              chartKind: chartKind,
              title: 'Revenue ($chartKind)',
              points: const [
                ChartPointNode(label: 'Jan', value: 1200),
                ChartPointNode(label: 'Feb', value: 1850),
                ChartPointNode(label: 'Mar', value: 2400),
              ],
              comparePoints: chartKind == 'pie'
                  ? null
                  : const [1000, 1500, 2100],
              seriesName: '2026',
              compareName: '2025',
              sourceNeuronUri: 'neuron://local/apps/data/revenue',
              from: 'Data Studio',
              prefix: '€',
            );
            final ctx = NodeContext(
              view: 'local/apps/data/view',
              nodeKeys: {'chart:Revenue ($chartKind)': 'pick_point'},
              send: (event) async {
                clickedPoint = event['value'] as String?;
                return null;
              },
            );

            await pumpSurface(
              tester,
              Padding(
                padding: const EdgeInsets.all(24),
                child: NodeRenderer.render(node, ctx),
              ),
              brightness: brightness,
            );
            expect(tester.takeException(), isNull);
            expect(find.byType(ChartCard), findsOneWidget);

            // Hover over plot / legend to trigger tooltip readout:
            final gesture = await tester.createGesture(
              kind: PointerDeviceKind.mouse,
            );
            if (chartKind == 'pie') {
              final legendRow = find.byKey(const ValueKey('pie_legend_1'));
              final febPos = tester.getCenter(legendRow);
              await gesture.addPointer(location: febPos);
              await tester.pump();
              expect(
                find.byKey(const ValueKey('chart_tooltip')),
                findsOneWidget,
              );
              await tester.tap(legendRow);
              await tester.pump();
              expect(clickedPoint, 'Feb');
            } else {
              final paintRect = tester.getRect(
                find
                    .descendant(
                      of: find.byType(ChartCard),
                      matching: find.byType(CustomPaint),
                    )
                    .first,
              );
              final probe = Offset(
                paintRect.left + paintRect.width * 0.55,
                paintRect.center.dy,
              );
              await gesture.addPointer(location: probe);
              await gesture.moveTo(probe);
              await tester.pump();
              expect(
                find.byKey(const ValueKey('chart_tooltip')),
                findsOneWidget,
              );
              await tester.tapAt(probe);
              await tester.pump();
              expect(clickedPoint, isNotNull);
            }
            await gesture.removePointer();
          }
        },
      );

      testWidgets(
        'DataTable supports column sorting, >= 8 rows quick client filter, empty filter & empty table states (${brightness.name})',
        (tester) async {
          const tableNode = DataTableNode(
            title: 'Accounts',
            columns: ['Account', 'MRR'],
            rows: [
              ['Havbris', '€4,200'],
              ['Nordlys', '€12,800'],
              ['Fjordkraft', '€950'],
              ['Solvind', '€6,100'],
              ['Bergen Labs', '€3,400'],
              ['Kystlinje', '€8,900'],
              ['Vesterhav', '€2,150'],
              ['Polaris', '€15,000'],
            ],
            sourceNeuronUri: null,
            from: 'Salesforce',
          );
          const ctx = NodeContext(view: 'local/apps/data/view');

          await pumpSurface(
            tester,
            SingleChildScrollView(
              padding: const EdgeInsets.all(24),
              child: NodeRenderer.render(tableNode, ctx),
            ),
            brightness: brightness,
          );
          expect(tester.takeException(), isNull);
          expect(find.byType(TablePreviewCard), findsOneWidget);
          // >= 8 rows -> quick filter input is present!
          expect(
            find.byKey(const ValueKey('table_quick_filter_input')),
            findsOneWidget,
          );

          // Click numeric MRR column header -> sorts descending (Polaris €15,000 first)
          await tester.tap(find.byKey(const ValueKey('table_sort_header_1')));
          await tester.pump();
          final polarisY = tester.getTopLeft(find.text('Polaris')).dy;
          final nordlysY = tester.getTopLeft(find.text('Nordlys')).dy;
          expect(polarisY, lessThan(nordlysY));

          // Click again -> sorts ascending (Fjordkraft €950 first)
          await tester.tap(find.byKey(const ValueKey('table_sort_header_1')));
          await tester.pump();
          expect(find.text('Fjordkraft'), findsOneWidget);

          // Type client filter query matching 1 row:
          await tester.enterText(
            find.byKey(const ValueKey('table_quick_filter_input')),
            'havb',
          );
          await tester.pump();
          expect(find.text('1 of 8 rows'), findsOneWidget);
          expect(find.text('Havbris'), findsOneWidget);
          expect(find.text('Nordlys'), findsNothing);

          // Type client filter query matching 0 rows -> crisp empty filter state + clear action:
          await tester.enterText(
            find.byKey(const ValueKey('table_quick_filter_input')),
            'NoSuchCompany',
          );
          await tester.pump();
          expect(
            find.byKey(const ValueKey('table_empty_filter_state')),
            findsOneWidget,
          );
          await tester.tap(
            find.byKey(const ValueKey('table_clear_filter_action')),
          );
          await tester.pump();
          expect(
            find.byKey(const ValueKey('table_empty_filter_state')),
            findsNothing,
          );

          // Initial empty DataTable state:
          const emptyNode = DataTableNode(
            title: 'Empty',
            columns: ['Name'],
            rows: [],
            sourceNeuronUri: null,
            from: 'Local',
          );
          await pumpSurface(
            tester,
            NodeRenderer.render(emptyNode, ctx),
            brightness: brightness,
          );
          expect(find.text('Nothing matches.'), findsOneWidget);
        },
      );
    }
  });

  group(
    'UK-12..UK-15 Field UX, InfoBar, Tabs, EmptyState & Virtualized Collection',
    () {
      testWidgets(
        'FieldNode supports search/text clear (x) + Esc clear, and secret eye/eye-off toggle',
        (tester) async {
          final sentInputs = <(String, String)>[];
          final ctx = NodeContext(
            view: 'local/apps/settings/view',
            send: (event) async {
              if (event['kind'] == 'field') {
                sentInputs.add((
                  event['name'] as String,
                  event['value'] as String,
                ));
              }
              return null;
            },
          );

          const viewNode = ColumnNode(
            gap: 12,
            padding: 16,
            extents: null,
            children: [
              FieldNode(
                key: 'q',
                name: 'apps/settings/query',
                fieldKind: FieldKind.search,
                placeholder: 'Search settings…',
                onSubmit: null,
                automationName: 'Search',
              ),
              FieldNode(
                key: 'api_key',
                name: 'apps/settings/apiKey',
                fieldKind: FieldKind.secret,
                placeholder: 'sk-...',
                onSubmit: null,
                automationName: 'API key',
              ),
            ],
          );

          await pumpSurface(
            tester,
            NodeRenderer.render(viewNode, ctx),
            brightness: Brightness.light,
          );

          // Search field: initially no clear button; typing reveals clear (x) button.
          expect(
            find.byKey(const ValueKey('ui_kit_field_clear')),
            findsNothing,
          );
          final searchField = find.byType(TextField).first;
          await tester.enterText(searchField, 'anthropic');
          await tester.pump();
          expect(
            find.byKey(const ValueKey('ui_kit_field_clear')),
            findsOneWidget,
          );

          // Clicking clear (x) empties the search field:
          await tester.tap(find.byKey(const ValueKey('ui_kit_field_clear')));
          await tester.pump(const Duration(milliseconds: 300));
          expect(
            tester.widget<TextField>(searchField).controller?.text,
            isEmpty,
          );
          expect(sentInputs.last.$2, isEmpty);

          // Typing again and pressing Escape also clears:
          await tester.enterText(searchField, 'ollama');
          await tester.pump();
          await tester.sendKeyEvent(LogicalKeyboardKey.escape);
          await tester.pump(const Duration(milliseconds: 300));
          expect(
            tester.widget<TextField>(searchField).controller?.text,
            isEmpty,
          );

          // Secret field: obscured by default, toggle switches obscureText on/off:
          final secretField = find.byType(TextField).last;
          expect(tester.widget<TextField>(secretField).obscureText, isTrue);
          await tester.tap(
            find.byKey(const ValueKey('ui_kit_field_secret_toggle')),
          );
          await tester.pump();
          expect(tester.widget<TextField>(secretField).obscureText, isFalse);
          await tester.tap(
            find.byKey(const ValueKey('ui_kit_field_secret_toggle')),
          );
          await tester.pump();
          expect(tester.widget<TextField>(secretField).obscureText, isTrue);
        },
      );

      testWidgets(
        'InfoBar places action inline and EmptyState infers app icon',
        (tester) async {
          String? lastKey;
          final ctx = NodeContext(
            view: 'local/apps/files/view',
            send: (event) async {
              lastKey = event['key'] as String?;
              return null;
            },
          );

          const node = ColumnNode(
            gap: 16,
            padding: 16,
            extents: null,
            children: [
              InfoBarNode(
                title: 'Sync paused',
                message: 'Reconnect storage to resume.',
                tone: 'working',
                close: null,
                key: '',
                automationName: '',
                action: ButtonNode(
                  key: 'reconnect',
                  label: 'Reconnect',
                  command: null,
                  intent: null,
                  enabled: true,
                  tone: 'neutral',
                  automationName: 'Reconnect',
                  tooltip: null,
                  visible: true,
                  style: 'secondary',
                  icon: 'refresh-cw',
                  small: true,
                ),
              ),
              EmptyStateNode(
                title: 'No files yet',
                body: 'Drop a document to get started.',
                suggestions: null,
              ),
            ],
          );

          await pumpSurface(
            tester,
            SingleChildScrollView(child: NodeRenderer.render(node, ctx)),
            brightness: Brightness.dark,
          );
          expect(tester.takeException(), isNull);

          // EmptyState inferred 'files' instead of hardcoded 'ino':
          final appIcon = tester.widget<IcAppIcon>(find.byType(IcAppIcon));
          expect(appIcon.appId, 'files');

          // InfoBar renders Reconnect inline inside Key('ui_info_bar'):
          expect(
            find.descendant(
              of: find.byKey(const Key('ui_info_bar')),
              matching: find.text('Reconnect'),
            ),
            findsOneWidget,
          );
          await tester.tap(find.text('Reconnect'));
          await tester.pump();
          expect(lastKey, 'reconnect');
        },
      );

      testWidgets(
        'Collection virtualizes 1,500 items lazily via ListView.builder without inflating all rows',
        (tester) async {
          final largeCollection = CollectionNode(
            entries: [
              for (var i = 0; i < 1500; i++)
                Entry(
                  id: 'item_$i',
                  title: 'Transaction #$i',
                  subtitle: 'Cleared',
                  trailing: '€${i * 10}',
                ),
            ],
          );

          await pumpSurface(
            tester,
            ViewHost(
              kind: 'view',
              name: 'local/apps/data/ledger',
              knowsApp: (_) => false,
              load: (_, _) async => {
                'generation': 1,
                'view': {
                  'title': 'Ledger',
                  'root': {
                    'kind': 'collection',
                    'entries': [
                      for (var i = 0; i < 1500; i++)
                        {
                          'id': 'item_$i',
                          'title': 'Transaction #$i',
                          'subtitle': 'Cleared',
                          'trailing': '€${i * 10}',
                        },
                    ],
                  },
                },
              },
            ),
            brightness: Brightness.light,
            width: 800,
            height: 600,
          );
          await tester.pump();

          expect(
            find.byKey(const ValueKey('virtualized_collection_list')),
            findsOneWidget,
          );
          expect(find.text('Transaction #0'), findsOneWidget);
          // Item #1499 must NOT be built eagerly while at scroll offset 0!
          expect(find.text('Transaction #1499'), findsNothing);
          expect(largeCollection.entries.length, 1500);

          // Scroll down lazily -> distant item builds smoothly on demand!
          final listFinder = find.byKey(
            const ValueKey('virtualized_collection_list'),
          );
          final scrollable = tester.state<ScrollableState>(
            find.descendant(of: listFinder, matching: find.byType(Scrollable)),
          );
          scrollable.position.jumpTo(scrollable.position.maxScrollExtent);
          await tester.pump();
          // Repeat once more after extent estimate refines:
          scrollable.position.jumpTo(scrollable.position.maxScrollExtent);
          await tester.pump();
          expect(find.text('Transaction #1499'), findsOneWidget);
          expect(find.text('Transaction #0'), findsNothing);
        },
      );
    },
  );

  group('Kit v2 Layout Physics & WinUI-Grade Controls', () {
    testWidgets(
      'HeaderBar Row keeps buttons at natural width and Column never stretches standalone buttons',
      (tester) async {
        const ctx = NodeContext(view: 'local/apps/studio/view');
        final headerAndColumn = Node.fromJson({
          'kind': 'column',
          'gap': 12,
          'children': [
            {
              'kind': 'row',
              'gap': 8,
              'children': [
                {
                  'kind': 'heading',
                  'value': 'PhotoCraft',
                  'level': 2,
                  'subtitle': 'Ready',
                },
                {'kind': 'spacer', 'size': 0}, // FlexSpacer()
                {
                  'kind': 'button',
                  'key': 'mode-mcp',
                  'label': 'MCP',
                  'small': true,
                },
                {
                  'kind': 'button',
                  'key': 'preset-cutout',
                  'label': 'Remove Background',
                  'tone': 'accent',
                  'small': true,
                },
              ],
            },
            {
              'kind': 'button',
              'key': 'clear-sel',
              'label': 'Clear selection',
              'small': true,
            },
            {
              'kind': 'card',
              'title': 'Inspector Summary',
              'body': 'Stretches full width',
              'children': <Map<String, dynamic>>[],
            },
          ],
        });

        await pumpSurface(
          tester,
          NodeRenderer.render(headerAndColumn, ctx),
          brightness: Brightness.dark,
          width: 1100,
        );
        expect(tester.takeException(), isNull);

        // Standalone button in Column must stay natural width (< 260px), never 1100px!
        final clearSize = tester.getSize(find.text('Clear selection'));
        expect(clearSize.width, lessThan(220));

        // Trailing button in HeaderBar Row is pushed to the right half by SpacerNode(0)!
        final headingLeft = tester.getTopLeft(find.text('PhotoCraft')).dx;
        final mcpLeft = tester.getTopLeft(find.text('MCP')).dx;
        expect(mcpLeft, greaterThan(headingLeft + 400));
      },
    );

    testWidgets(
      'Field humanizes neuron paths and honours __label, __value, __helper, __error & Surface kinds',
      (tester) async {
        final ctx = NodeContext(
          view: 'local/apps/creator/view',
          send: (e) async => null,
        );

        final node = Node.fromJson({
          'kind': 'column',
          'gap': 12,
          'children': [
            {
              'kind': 'field',
              'key': 'raw-path-field',
              'name': 'owner/apps/files/folder_name',
              'fieldKind': 'text',
              'placeholder': 'Type name…',
            },
            {
              'kind': 'field',
              'key': 'meta-field',
              'name': 'apps/creator/describe',
              'fieldKind': 'text',
              'placeholder': 'Describe unit…',
              'onSubmit': {
                'target': 'creator',
                'signal': 'make',
                'args': {
                  'uiLabel': 'Unit Goal',
                  'uiValue': 'Pomodoro Sprint',
                  'uiHelper': 'Compiled via Roslyn',
                },
              },
            },
            {
              'kind': 'surface',
              'surfaceKind': 'code:csharp',
              'title': 'UnitApp.cs',
              'subtitle': 'public sealed record CounterState(int Value = 0);',
              'padding': 180,
              'children': <Map<String, dynamic>>[],
            },
            {
              'kind': 'surface',
              'surfaceKind': 'expander:closed',
              'title': 'Quick Write & Clipboard',
              'subtitle': 'Create a text file',
              'padding': 12,
              'children': [
                {'kind': 'text', 'value': 'Hidden until expanded'},
              ],
            },
          ],
        });

        await pumpSurface(
          tester,
          NodeRenderer.render(node, ctx),
          brightness: Brightness.dark,
          width: 960,
        );
        expect(tester.takeException(), isNull);

        // 1. Slash path `owner/apps/files/folder_name` humanized to `Folder name`
        expect(find.text('Folder name'), findsOneWidget);
        expect(find.text('owner/apps/files/folder_name'), findsNothing);

        // 2. Explicit `__label`, `__value`, and `__helper` rendered cleanly
        expect(find.text('Unit Goal'), findsOneWidget);
        expect(find.text('Pomodoro Sprint'), findsOneWidget);
        expect(find.text('Compiled via Roslyn'), findsOneWidget);

        // 4. Code viewer renders title + line 1 + source
        expect(find.text('UnitApp.cs'), findsOneWidget);

        // 5. Closed expander hides body until tapped, then reveals it
        expect(find.text('Hidden until expanded'), findsNothing);
        await tester.tap(find.text('Quick Write & Clipboard'));
        await tester.pump();
        expect(find.text('Hidden until expanded'), findsOneWidget);
      },
    );
  });
}
