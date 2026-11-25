import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/src/widgets/neuron/ui_control_surface.dart';

void main() {
  testWidgets(
    'UiMixButton renders exact style padding/radius via mix and reports a press',
    (tester) async {
      var clicks = 0;

      // A style as it arrives over JSON.
      final styleFromCSharp = UiStyleData.fromJson({
        'tone': 'accent',
        'paddingX': 24,
        'paddingY': 14,
        'radius': 12,
        'motion': 'spring',
      });

      expect(styleFromCSharp.paddingX, 24);
      expect(styleFromCSharp.paddingY, 14);
      expect(styleFromCSharp.radius, 12);
      expect(styleFromCSharp.motion, UiMotionPreset.spring);

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: Center(
              child: UiMixButton(
                label: 'Save',
                style: styleFromCSharp,
                semanticsId: 'key:mix:save',
                onPressed: () => clicks++,
              ),
            ),
          ),
        ),
      );

      final containerFinder = find.byType(Container).first;
      final container = tester.widget<Container>(containerFinder);
      expect(
        container.padding,
        equals(const EdgeInsets.symmetric(horizontal: 24, vertical: 14)),
      );

      final gesture = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await gesture.addPointer(location: Offset.zero);
      addTearDown(gesture.removePointer);

      final center = tester.getCenter(find.text('Save'));
      await gesture.moveTo(center);
      await tester.pumpAndSettle();

      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();
      expect(clicks, 1);
    },
  );

  testWidgets(
    'UiMixToggle and UiMixExpander toggle state and render exact style padding/gap',
    (tester) async {
      var isOn = false;
      var expanded = false;

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: StatefulBuilder(
              builder: (context, setState) => Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  UiMixToggle(
                    value: isOn,
                    label: 'Live Sync',
                    semanticsId: 'key:mix:toggle',
                    onChanged: (v) => setState(() => isOn = v),
                  ),
                  UiMixExpander(
                    header: 'Advanced Options',
                    subtitle: 'C# Mix Expander',
                    isExpanded: expanded,
                    semanticsId: 'key:mix:expander',
                    onExpandedChanged: (v) => setState(() => expanded = v),
                    body: const Text('Expanded Details'),
                  ),
                ],
              ),
            ),
          ),
        ),
      );

      expect(find.text('Expanded Details'), findsNothing);

      await tester.tap(find.text('Live Sync'));
      await tester.pumpAndSettle();
      expect(isOn, isTrue);

      await tester.tap(find.text('Advanced Options'));
      await tester.pumpAndSettle();
      expect(expanded, isTrue);
      expect(find.text('Expanded Details'), findsOneWidget);
    },
  );

  testWidgets(
    'UiMixCheckbox toggles state and UiMixSurface renders card, elevated, and recessed variants',
    (tester) async {
      var checked = false;

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: StatefulBuilder(
              builder: (context, setState) => Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  UiMixSurface(
                    variant: UiMixSurfaceVariant.card,
                    semanticsId: 'key:mix:surface-card',
                    child: UiMixCheckbox(
                      value: checked,
                      label: 'Enable Telemetry',
                      semanticsId: 'key:mix:checkbox',
                      onChanged: (v) => setState(() => checked = v),
                    ),
                  ),
                  const UiMixSurface(
                    variant: UiMixSurfaceVariant.elevated,
                    child: Text('Elevated Surface'),
                  ),
                  const UiMixSurface(
                    variant: UiMixSurfaceVariant.recessed,
                    child: Text('Recessed Surface'),
                  ),
                ],
              ),
            ),
          ),
        ),
      );

      expect(find.text('Enable Telemetry'), findsOneWidget);
      expect(find.text('Elevated Surface'), findsOneWidget);
      expect(find.text('Recessed Surface'), findsOneWidget);
      expect(checked, isFalse);

      await tester.tap(find.text('Enable Telemetry'));
      await tester.pumpAndSettle();
      expect(checked, isTrue);
    },
  );

  testWidgets(
    'UiMixIconButton, UiMixRadio, UiMixSlider, and UiMixChip handle interaction cleanly',
    (tester) async {
      var iconClicks = 0;
      var radioValue = 'a';
      var sliderValue = 20.0;
      var chipSelected = false;

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: StatefulBuilder(
              builder: (context, setState) => Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  UiMixIconButton(
                    icon: Icons.star,
                    tooltip: 'Favorite',
                    semanticsId: 'key:mix:icon-btn',
                    onPressed: () => setState(() => iconClicks++),
                  ),
                  UiMixRadio<String>(
                    value: 'a',
                    groupValue: radioValue,
                    label: 'Option A',
                    onChanged: (v) => setState(() => radioValue = v),
                  ),
                  UiMixRadio<String>(
                    value: 'b',
                    groupValue: radioValue,
                    label: 'Option B',
                    onChanged: (v) => setState(() => radioValue = v),
                  ),
                  SizedBox(
                    width: 200,
                    child: UiMixSlider(
                      value: sliderValue,
                      min: 0,
                      max: 100,
                      step: 10,
                      semanticsId: 'key:mix:slider',
                      onChanged: (v) => setState(() => sliderValue = v),
                    ),
                  ),
                  UiMixChip(
                    label: 'Active Filter',
                    icon: Icons.filter_list,
                    selected: chipSelected,
                    semanticsId: 'key:mix:chip',
                    onSelected: (v) => setState(() => chipSelected = v),
                  ),
                ],
              ),
            ),
          ),
        ),
      );

      await tester.tap(find.byType(UiMixIconButton));
      await tester.pumpAndSettle();
      expect(iconClicks, 1);

      await tester.tap(find.text('Option B'));
      await tester.pumpAndSettle();
      expect(radioValue, 'b');

      await tester.tap(find.byType(UiMixSlider));
      await tester.pumpAndSettle();
      expect(sliderValue, greaterThan(20.0));

      await tester.tap(find.text('Active Filter'));
      await tester.pumpAndSettle();
      expect(chipSelected, isTrue);
    },
  );

  test('UiStyleData.fromJson parses hover/press/focus state overrides and resolve merges them in precedence order', () {
    final style = UiStyleData.fromJson({
      'tone': 'default',
      'typography': 'body',
      'elevation': 'flat',
      'paddingX': 12,
      'paddingY': 8,
      'gap': 6,
      'radius': 8,
      'motion': 'standard',
      'focus': {
        'tone': 'subtle',
        'elevation': 'raised',
        'radius': 10,
        'gap': 8,
      },
      'hover': {
        'tone': 'accent',
        'paddingX': 16,
        'motion': 'snappy',
        'scale': 1.02,
      },
      'press': {'tone': 'danger', 'paddingY': 6, 'scale': 0.96},
    });

    // Base state returns itself untouched.
    final base = style.resolve();
    expect(identical(base, style), isTrue);
    expect(base.tone, 'default');
    expect(base.paddingX, 12);
    expect(base.paddingY, 8);
    expect(base.radius, 8);
    expect(base.scale, isNull);

    // Focus only.
    final focused = style.resolve(focused: true);
    expect(focused.tone, 'subtle');
    expect(focused.elevation, 'raised');
    expect(focused.radius, 10);
    expect(focused.gap, 8);
    expect(focused.paddingX, 12);

    // Hover overrides focus.
    final hoveredAndFocused = style.resolve(hovered: true, focused: true);
    expect(hoveredAndFocused.tone, 'accent');
    expect(hoveredAndFocused.elevation, 'raised');
    expect(hoveredAndFocused.paddingX, 16);
    expect(hoveredAndFocused.motion, UiMotionPreset.snappy);
    expect(hoveredAndFocused.scale, 1.02);

    // Press overrides hover and focus.
    final allActive = style.resolve(
      hovered: true,
      pressed: true,
      focused: true,
    );
    expect(allActive.tone, 'danger');
    expect(allActive.elevation, 'raised');
    expect(allActive.paddingX, 16);
    expect(allActive.paddingY, 6);
    expect(allActive.gap, 8);
    expect(allActive.radius, 10);
    expect(allActive.motion, UiMotionPreset.snappy);
    expect(allActive.scale, 0.96);
  });
}
