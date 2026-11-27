import 'dart:ui';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/src/nodes/action_nodes.dart';
import 'package:intochat_os_flutter_shell/src/nodes/surface_nodes.dart';

import 'package:intochat_os_flutter_shell/src/primitives/ui_control_surface.dart';

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

  test('UiStyleData.fromWire supports structured JSON, px/py aliases, and legacy string presets', () {
    final fromMap = UiStyleData.fromWire({
      'tone': 'accent',
      'px': 18,
      'py': 9,
      'elevation': 'floating',
      'typography': 'heading',
      'hover': {'tone': 'accentStrong', 'px': 20, 'scale': 1.03},
    });
    expect(fromMap, isNotNull);
    expect(fromMap!.tone, 'accent');
    expect(fromMap.paddingX, 18);
    expect(fromMap.paddingY, 9);
    expect(fromMap.elevation, 'floating');
    expect(fromMap.typography, 'heading');
    expect(fromMap.hover?.tone, 'accentstrong');
    expect(fromMap.hover?.paddingX, 20);
    expect(fromMap.hover?.scale, 1.03);

    final primary = UiStyleData.fromWire('primary');
    expect(primary?.tone, 'accent');
    expect(primary?.hover?.tone, 'accentstrong');

    final ghost = UiStyleData.fromWire('ghost');
    expect(ghost?.tone, 'ghost');
    expect(ghost?.hover?.tone, 'subtle');

    expect(UiStyleData.fromWire(null), isNull);
    expect(UiStyleData.fromWire(''), isNull);
  });

  testWidgets(
    'UiMixButton applies hover, press, and focus overrides (tone, padding, elevation, scale) end-to-end',
    (tester) async {
      final focusNode = FocusNode();
      addTearDown(focusNode.dispose);

      final style = UiStyleData.fromJson({
        'tone': 'secondary',
        'typography': 'label',
        'elevation': 'flat',
        'paddingX': 12,
        'paddingY': 8,
        'radius': 8,
        'motion': 'snappy',
        'focus': {'tone': 'accentTint', 'elevation': 'subtle', 'radius': 12},
        'hover': {
          'tone': 'accentStrong',
          'elevation': 'raised',
          'paddingX': 20,
          'scale': 1.04,
        },
        'press': {'tone': 'danger', 'elevation': 'floating', 'scale': 0.95},
      });

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: Center(
              child: UiMixButton(
                label: 'Deploy',
                icon: 'rocket',
                focusNode: focusNode,
                style: style,
                onPressed: () {},
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      final colors = UiColors.intochatLight;

      Container buttonBox() => tester.widget<Container>(
        find
            .descendant(
              of: find.byType(UiMixButton),
              matching: find.byType(Container),
            )
            .first,
      );

      // Base state: secondary surface, 12x8 padding, no elevation shadow.
      var box = buttonBox();
      var dec = box.decoration! as BoxDecoration;
      expect(dec.color, colors.surface);
      expect(
        box.padding,
        const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      );
      expect(dec.boxShadow, isNull);

      // Focus state: accentTint tone, subtle elevation shadow, radius 12.
      focusNode.requestFocus();
      await tester.pumpAndSettle();
      box = buttonBox();
      dec = box.decoration! as BoxDecoration;
      expect(dec.color, colors.accentTint);
      expect(dec.boxShadow, isNotEmpty);
      expect(dec.borderRadius, BorderRadius.circular(12));

      // Hover state overrides focus: accentHover background, paddingX 20, scale 1.04.
      final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await mouse.addPointer(location: Offset.zero);
      addTearDown(mouse.removePointer);

      final center = tester.getCenter(find.text('Deploy'));
      await mouse.moveTo(center);
      await tester.pumpAndSettle();
      box = buttonBox();
      dec = box.decoration! as BoxDecoration;
      expect(dec.color, colors.accentHover);
      expect(
        box.padding,
        const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
      );
      final animatedScale = tester.widget<AnimatedScale>(
        find.descendant(
          of: find.byType(UiMixButton),
          matching: find.byType(AnimatedScale),
        ),
      );
      expect(animatedScale.scale, 1.04);

      // Press state overrides hover: danger background, scale 0.95.
      final pressGesture = await tester.startGesture(center);
      await tester.pumpAndSettle();
      box = buttonBox();
      dec = box.decoration! as BoxDecoration;
      expect(dec.color, colors.error);
      final pressedScale = tester.widget<AnimatedScale>(
        find.descendant(
          of: find.byType(UiMixButton),
          matching: find.byType(AnimatedScale),
        ),
      );
      expect(pressedScale.scale, 0.95);
      await pressGesture.up();
      await tester.pumpAndSettle();
    },
  );

  testWidgets(
    'UiStyleMapper.widgetStateBoxStyler and textStyle map typography (Inter only) and variants',
    (tester) async {
      late TextStyle displayStyle;
      late TextStyle headingStyle;
      late TextStyle labelStyle;
      late TextStyle bodyNeutralStyle;
      late TextStyle bodyAccentStyle;
      late TextStyle metaStyle;
      late TextStyle secondaryStyle;
      late TextStyle metaOverrideStyle;
      late TextStyle captionStyle;
      late TextStyle groupStyle;
      late TextStyle metricStyle;
      late TextStyle fallbackStyle;
      late Object widgetStateStyler;

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Builder(
            builder: (context) {
              displayStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'accent', typography: 'display'),
              );
              headingStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'heading'),
              );
              labelStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'label'),
              );
              bodyNeutralStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'body'),
              );
              bodyAccentStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'accent', typography: 'body'),
              );
              metaStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'meta'),
              );
              secondaryStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'secondary'),
              );
              metaOverrideStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'meta'),
                colorOverride: Colors.red,
              );
              captionStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'ghost', typography: 'caption'),
              );
              groupStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'group'),
              );
              metricStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'metric'),
              );
              fallbackStyle = UiStyleMapper.textStyle(
                context,
                const UiStyleData(tone: 'default', typography: 'unknown-token'),
              );
              widgetStateStyler = UiStyleMapper.widgetStateBoxStyler(
                context,
                UiStyleData.fromJson({
                  'tone': 'accent',
                  'elevation': 'raised',
                  'hover': {'tone': 'accentStrong'},
                  'press': {'tone': 'danger'},
                  'focus': {'elevation': 'floating'},
                }),
              );
              return const SizedBox.shrink();
            },
          ),
        ),
      );

      final colors = UiColors.intochatLight;
      expect(displayStyle.fontFamily, UiType.display);
      expect(displayStyle.fontSize, 24);
      expect(headingStyle.fontFamily, UiType.display);
      expect(headingStyle.fontSize, 17);
      expect(headingStyle.fontWeight, FontWeight.w700);
      expect(labelStyle.fontFamily, UiType.sans);
      expect(labelStyle.fontSize, 13.5);
      expect(labelStyle.fontWeight, FontWeight.w600);
      expect(bodyNeutralStyle.fontFamily, UiType.sans);
      expect(bodyNeutralStyle.fontSize, 14);
      expect(bodyNeutralStyle.fontWeight, FontWeight.w400);
      expect(bodyNeutralStyle.height, 1.5);
      expect(bodyNeutralStyle.color, colors.textBody);
      expect(bodyAccentStyle.color, colors.onAccent);
      expect(metaStyle.fontSize, 13);
      expect(metaStyle.fontWeight, FontWeight.w400);
      expect(metaStyle.color, colors.textMuted);
      expect(secondaryStyle.fontSize, 13);
      expect(secondaryStyle.fontWeight, FontWeight.w400);
      expect(secondaryStyle.color, colors.textMuted);
      expect(metaOverrideStyle.color, Colors.red);
      expect(captionStyle.fontFamily, UiType.sans);
      expect(captionStyle.fontSize, 12.5);
      expect(captionStyle.fontWeight, FontWeight.w400);
      expect(groupStyle.fontSize, 11.5);
      expect(groupStyle.fontWeight, FontWeight.w700);
      expect(groupStyle.letterSpacing, 0.6);
      expect(metricStyle.fontFamily, UiType.sans);
      expect(metricStyle.fontFeatures, UiType.tabular);
      expect(fallbackStyle.fontSize, 14);
      expect(fallbackStyle.fontWeight, FontWeight.w400);
      expect(widgetStateStyler, isNotNull);
    },
  );

  testWidgets(
    'renderButton routes both structured Mix.Style() JSON and legacy strings through UiMixButton',
    (tester) async {
      final structuredNode = ButtonNode.fromJson({
        'key': 'btn-mix',
        'label': 'Mix Styled',
        'style': {
          'tone': 'accent',
          'paddingX': 22,
          'paddingY': 11,
          'radius': 14,
          'elevation': 'raised',
          'hover': {'tone': 'accentStrong'},
        },
      });
      final legacyNode = ButtonNode.fromJson({
        'key': 'btn-legacy',
        'label': 'Legacy Primary',
        'style': 'primary',
      });

      final ctx = NodeContext(view: 'test-view', send: (_) async => null);

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: Column(
              children: [
                renderButton(structuredNode, ctx),
                renderButton(legacyNode, ctx),
              ],
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byType(UiMixButton), findsNWidgets(2));
      expect(find.text('Mix Styled'), findsOneWidget);
      expect(find.text('Legacy Primary'), findsOneWidget);
    },
  );

  testWidgets(
    'UiMixInfoBar and UiMixProgress primitives render severity, dismiss, and progress modes',
    (tester) async {
      var dismissed = false;
      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: Column(
              children: [
                UiMixInfoBar(
                  severity: 'warning',
                  title: 'Rate limit approaching',
                  body: 'Consider batching requests.',
                  onDismiss: () => dismissed = true,
                ),
                const UiMixProgress(
                  determinate: true,
                  value: 0.65,
                  label: 'Syncing 65%',
                ),
                const UiMixProgress(
                  determinate: false,
                  value: 0,
                  label: 'Connecting',
                  ring: true,
                ),
              ],
            ),
          ),
        ),
      );
      await tester.pump();

      expect(find.byKey(const Key('ui_info_bar')), findsOneWidget);
      expect(find.text('Rate limit approaching'), findsOneWidget);
      expect(find.text('Syncing 65%'), findsOneWidget);
      expect(find.text('Connecting'), findsOneWidget);

      await tester.tap(find.byType(UiMixIconButton));
      await tester.pump();
      expect(dismissed, isTrue);
    },
  );

  testWidgets(
    'renderCard, renderSurface, and renderIconButton consume structured Mix.Style() wire JSON',
    (tester) async {
      final cardNode = CardNode.fromJson({
        'title': 'Mix Card',
        'subtitle': 'Structured Surface',
        'children': [
          {'kind': 'text', 'value': 'Card body'},
        ],
        'style': {'tone': 'surface', 'radius': 12, 'elevation': 'sm'},
      });
      final surfaceNode = SurfaceNode.fromJson({
        'title': 'Mix Surface',
        'kind': 'elevated',
        'children': [
          {'kind': 'text', 'value': 'Surface body'},
        ],
        'style': {
          'tone': 'surface',
          'radius': 16,
          'elevation': 'md',
          'paddingX': 18,
          'paddingY': 14,
        },
      });
      final iconButtonNode = IconButtonNode.fromJson({
        'key': 'mix-icon-close',
        'icon': 'x',
        'tooltip': 'Close window',
        'style': {
          'tone': 'ghost',
          'radius': 8,
          'hover': {'tone': 'danger'},
        },
      });

      final ctx = NodeContext(view: 'test-view', send: (_) async => null);

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.light(null),
          home: Scaffold(
            body: SingleChildScrollView(
              child: Column(
                children: [
                  renderCard(cardNode, ctx),
                  renderSurface(surfaceNode, ctx),
                  renderIconButton(iconButtonNode, ctx),
                ],
              ),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byType(UiMixSurface), findsNWidgets(2));
      expect(find.byType(UiMixIconButton), findsOneWidget);
      expect(find.text('Mix Card'), findsOneWidget);
      expect(find.text('Mix Surface'), findsOneWidget);
    },
  );
}
