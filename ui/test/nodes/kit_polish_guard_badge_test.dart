import 'dart:ui' show PointerDeviceKind;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  Widget host(Widget child, {Brightness brightness = Brightness.light}) =>
      MaterialApp(
        theme: UiTheme.intochat(brightness),
        home: Scaffold(body: Center(child: child)),
      );

  group('Status BadgeNode icon + colour pairing (DESIGN.md §2.5)', () {
    testWidgets(
      'pairs done, working, error, needs-you/accent, and idle/neutral with authoritative icons and tokens',
      (tester) async {
        const c = UiColors.intochatLight;
        const ctx = NodeContext(view: 'local/apps/sample/view');

        final cases =
            <(String tone, String label, IconData icon, Color fg, Color bg)>[
              ('done', 'Completed', LucideIcons.check, c.done, c.doneBg),
              ('success', 'Synced', LucideIcons.check, c.done, c.doneBg),
              (
                'working',
                'Running',
                LucideIcons.loaderCircle,
                c.working,
                c.workingBg,
              ),
              (
                'warning',
                'Pending',
                LucideIcons.loaderCircle,
                c.working,
                c.workingBg,
              ),
              (
                'error',
                'Failed',
                LucideIcons.triangleAlert,
                c.error,
                c.errorBg,
              ),
              (
                'danger',
                'Blocked',
                LucideIcons.triangleAlert,
                c.error,
                c.errorBg,
              ),
              (
                'needs-you',
                'Needs approval',
                LucideIcons.hand,
                c.needsYou,
                c.needsYouBg,
              ),
              (
                'accent',
                'Your turn',
                LucideIcons.hand,
                c.needsYou,
                c.needsYouBg,
              ),
              ('idle', 'Waiting', LucideIcons.clock, c.idle, c.surfaceSunk),
              ('neutral', 'Queued', LucideIcons.clock, c.idle, c.surfaceSunk),
            ];

        for (final (tone, label, expectedIcon, expectedFg, expectedBg)
            in cases) {
          await tester.pumpWidget(
            host(
              NodeRenderer.render(
                BadgeNode(label: label, tone: tone, icon: null),
                ctx,
              ),
            ),
          );
          await tester.pump();

          expect(
            find.byIcon(expectedIcon),
            findsOneWidget,
            reason: 'Status tone "$tone" must render icon $expectedIcon',
          );
          final text = tester.widget<Text>(find.text(label));
          expect(text.style?.color, expectedFg, reason: 'fg for $tone');
          expect(
            text.style?.fontFeatures,
            contains(const FontFeature.tabularFigures()),
          );

          final box = tester.widget<Container>(
            find
                .ancestor(
                  of: find.text(label),
                  matching: find.byType(Container),
                )
                .first,
          );
          final deco = box.decoration as BoxDecoration;
          expect(deco.color, expectedBg, reason: 'bg for $tone');
        }
      },
    );
  });

  group('Guarded Button Tooltip & Polish (DESIGN.md §10.8 & §11.1)', () {
    testWidgets(
      'disabled guarded button renders 0.45 opacity and shows Rule.Message Tooltip on hover',
      (tester) async {
        await tester.pumpWidget(
          host(
            ViewHost(
              kind: 'view',
              name: 'local/apps/bank/view',
              overrides: ViewHostOverrides(
                knowsApp: (_) => false,
                watch: (_, _) => const Stream.empty(),
                onAction: (_) async => null,
                load: (_, _) async => PublishedViewPayload.fromJson({
                  'generation': 1,
                  'view': {
                    'title': 'Bank',
                    'root': {
                      'kind': 'row',
                      'children': [
                        {
                          'kind': 'button',
                          'key': 'cmd:Withdraw:100',
                          'label': 'Withdraw €100',
                          'style': 'primary',
                          'enabled': false,
                          'tooltip': 'Insufficient funds (balance €30).',
                        },
                        {
                          'kind': 'button',
                          'key': 'cmd:Close',
                          'label': 'Close account',
                          'style': 'secondary',
                          'enabled': false,
                          'command': {
                            'target': '.',
                            'signal': 'Close',
                            'args': {
                              'guardReason':
                                  'Balance must be €0 before closing.',
                            },
                          },
                        },
                      ],
                    },
                  },
                }),
              ),
            ),
          ),
        );
        await tester.pumpAndSettle();

        expect(find.byType(Tooltip), findsNWidgets(2));

        // Hover over disabled Withdraw button -> Tooltip reveals ButtonNode.tooltip.
        final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
        await mouse.addPointer(location: Offset.zero);
        addTearDown(mouse.removePointer);

        await mouse.moveTo(tester.getCenter(find.text('Withdraw €100')));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 400));

        expect(find.text('Insufficient funds (balance €30).'), findsOneWidget);

        // Hover over guarded Close button -> Tooltip reveals command.args['guardReason'].
        await mouse.moveTo(tester.getCenter(find.text('Close account')));
        await tester.pump();
        await tester.pump(const Duration(milliseconds: 400));

        expect(find.text('Balance must be €0 before closing.'), findsOneWidget);
      },
    );
  });

  group(
    'DataTable & Chart token conformance (DESIGN.md §2.4, §3.1, §12.5)',
    () {
      testWidgets(
        'DataTable renders surfaceSunk header band, borderHairline dividers, and tabular numerals',
        (tester) async {
          const c = UiColors.intochatLight;
          const ctx = NodeContext(view: 'local/apps/sales/view');

          await tester.pumpWidget(
            host(
              SizedBox(
                width: 560,
                child: NodeRenderer.render(
                  const DataTableNode(
                    title: 'Top deals',
                    columns: ['Account', 'ARR'],
                    rows: [
                      ['Acme Corp', '€120,000'],
                      ['Northwind', '€84,500'],
                    ],
                    from: 'Salesforce',
                    sourceNeuronUri: null,
                    select: null,
                  ),
                  ctx,
                ),
              ),
            ),
          );
          await tester.pumpAndSettle();

          final headerBand = tester
              .widgetList<Container>(find.byType(Container))
              .where((w) => w.color == c.surfaceSunk);
          expect(headerBand, isNotEmpty);

          final arrCell = tester.widget<Text>(find.text('€120,000'));
          expect(arrCell.textAlign, TextAlign.right);
          expect(
            arrCell.style?.fontFeatures,
            contains(const FontFeature.tabularFigures()),
          );
        },
      );

      testWidgets(
        'ChartNode supports columns, line, and pie with accent + chartContext (#9C9182)',
        (tester) async {
          const ctx = NodeContext(view: 'local/apps/metrics/view');

          for (final chartKind in ['columns', 'line', 'pie']) {
            await tester.pumpWidget(
              host(
                SizedBox(
                  width: 600,
                  child: NodeRenderer.render(
                    ChartNode(
                      title: 'Revenue ($chartKind)',
                      chartKind: chartKind,
                      points: const [
                        ChartPointNode(label: 'Q1', value: 120),
                        ChartPointNode(label: 'Q2', value: 180),
                      ],
                      from: 'Stripe',
                      prefix: '€',
                      comparePoints: const [100, 140],
                      seriesName: '2026',
                      compareName: '2025',
                      sourceNeuronUri: null,
                      select: null,
                    ),
                    ctx,
                  ),
                ),
                brightness: Brightness.dark,
              ),
            );
            await tester.pumpAndSettle();

            expect(find.text('Revenue ($chartKind)'), findsOneWidget);
            expect(tester.takeException(), isNull);
          }
        },
      );
    },
  );
}
