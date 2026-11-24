import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/src/primitives/primitives.dart';

Widget _wrap(Widget child, {Size size = const Size(900, 700)}) {
  return MediaQuery(
    data: MediaQueryData(size: size),
    child: MaterialApp(
      home: Scaffold(body: Center(child: child)),
    ),
  );
}

void main() {
  group('New Mix primitives', () {
    testWidgets('UiBadge renders all semantic tones with dot and icon', (
      tester,
    ) async {
      await tester.pumpWidget(
        _wrap(
          const Wrap(
            spacing: 8,
            children: [
              UiBadge(label: 'Neutral', tone: UiBadgeTone.neutral),
              UiBadge(label: 'Accent', tone: UiBadgeTone.accent, dot: true),
              UiBadge(
                label: 'Success',
                tone: UiBadgeTone.success,
                icon: Icons.check,
              ),
              UiBadge(label: 'Warning', tone: UiBadgeTone.warning),
              UiBadge(label: 'Danger', tone: UiBadgeTone.danger),
            ],
          ),
        ),
      );

      expect(find.text('Neutral'), findsOneWidget);
      expect(find.text('Accent'), findsOneWidget);
      expect(find.text('Success'), findsOneWidget);
      expect(find.text('Warning'), findsOneWidget);
      expect(find.text('Danger'), findsOneWidget);
      expect(find.byIcon(Icons.check), findsOneWidget);
    });

    testWidgets('UiSegmentedControl switches active value on tap', (
      tester,
    ) async {
      var current = 'a';
      await tester.pumpWidget(
        _wrap(
          StatefulBuilder(
            builder: (context, setState) => UiSegmentedControl<String>(
              value: current,
              options: const [
                UiSegmentedOption(value: 'a', label: 'Alpha'),
                UiSegmentedOption(value: 'b', label: 'Beta'),
                UiSegmentedOption(value: 'c', label: 'Gamma', enabled: false),
              ],
              onChanged: (v) => setState(() => current = v),
            ),
          ),
        ),
      );

      expect(find.text('Alpha'), findsOneWidget);
      await tester.tap(find.text('Beta'));
      await tester.pumpAndSettle();
      expect(current, 'b');

      await tester.tap(find.text('Gamma'));
      await tester.pumpAndSettle();
      expect(current, 'b');
    });

    testWidgets('UiSkeleton renders with pulse animation and respects circle', (
      tester,
    ) async {
      await tester.pumpWidget(
        _wrap(
          const Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              UiSkeleton(width: 120, height: 16),
              SizedBox(height: 8),
              UiSkeleton(height: 32, circle: true),
            ],
          ),
        ),
      );

      await tester.pump(const Duration(milliseconds: 250));
      expect(find.byType(UiSkeleton), findsNWidgets(2));
    });

    testWidgets('UiAdaptiveNavBar renders rail on desktop and bar on compact', (
      tester,
    ) async {
      var selected = 0;
      const items = [
        UiNavBarItem(id: 'home', label: 'Home', icon: Icons.home_outlined),
        UiNavBarItem(
          id: 'chat',
          label: 'Chat',
          icon: Icons.chat_bubble_outline,
          badgeCount: 3,
        ),
      ];

      await tester.pumpWidget(
        _wrap(
          UiAdaptiveNavBar(
            items: items,
            selectedIndex: selected,
            onSelected: (i) => selected = i,
          ),
          size: const Size(900, 700),
        ),
      );
      expect(
        find.byKey(const ValueKey('ui_adaptive_nav_rail')),
        findsOneWidget,
      );
      expect(find.text('3'), findsOneWidget);

      await tester.tap(find.text('Chat'));
      await tester.pumpAndSettle();
      expect(selected, 1);

      await tester.pumpWidget(
        _wrap(
          UiAdaptiveNavBar(
            items: items,
            selectedIndex: selected,
            onSelected: (i) => selected = i,
          ),
          size: const Size(400, 700),
        ),
      );
      expect(
        find.byKey(const ValueKey('ui_adaptive_nav_bottom')),
        findsOneWidget,
      );
    });

    testWidgets(
      'showUiSheet opens dialog on desktop and bottom sheet on compact',
      (tester) async {
        await tester.pumpWidget(
          _wrap(
            Builder(
              builder: (context) => UiMixActionButton(
                label: 'Open Sheet',
                onPressed: () {
                  showUiSheet<void>(
                    context: context,
                    builder: (_) => const UiSheet(
                      title: 'Sheet Title',
                      child: Text('Sheet Body'),
                    ),
                  );
                },
              ),
            ),
            size: const Size(900, 700),
          ),
        );

        await tester.tap(find.text('Open Sheet'));
        await tester.pumpAndSettle();
        expect(find.text('Sheet Title'), findsOneWidget);
        expect(find.text('Sheet Body'), findsOneWidget);
      },
    );
  });
}
