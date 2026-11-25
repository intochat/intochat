import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _props = {'label': 'Depth', 'min': 0, 'max': 10, 'step': 1, 'unit': '%'};

class _Host extends StatefulWidget {
  const _Host({required this.props, required this.sent, this.value});

  final Map<String, dynamic> props;
  final List<Map<String, dynamic>> sent;
  final double? value;

  @override
  State<_Host> createState() => _HostState();
}

class _HostState extends State<_Host> {
  late Map<String, dynamic> props = widget.props;

  // The parent re-renders the slot with new props, as a controlled parent does.
  void render(Map<String, dynamic> next) => setState(() => props = next);

  @override
  Widget build(BuildContext context) => MaterialApp(
    theme: UiTheme.intochat(Brightness.light),
    home: Scaffold(
      body: SizedBox(
        width: 400,
        child: NodeRenderer.render(
          SliderViewNode(key: 'depth', value: widget.value),
          NodeContext(
            view: 'local/apps/creator/view',
            generation: 1,
            actNeuron: 'local/apps/creator',
            slotPath: 'depth',
            slotProps: props,
            send: (event) async {
              widget.sent.add(event);
              return {'delivered': true};
            },
          ),
        ),
      ),
    ),
  );
}

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> props, {
  double? value,
}) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(_Host(props: props, sent: sent, value: value));
  return sent;
}

void main() {
  testWidgets('it draws the neuron value with the unit', (tester) async {
    await _pump(tester, _props, value: 3);

    expect(find.text('Depth'), findsOneWidget);
    expect(find.text('3%'), findsOneWidget);
  });

  testWidgets('the parent value wins over the neuron', (tester) async {
    await _pump(tester, {..._props, 'value': 7}, value: 3);

    expect(find.text('7%'), findsOneWidget);
  });

  testWidgets('a drag sends one SetValue, on release', (tester) async {
    final sent = await _pump(tester, _props, value: 0);

    final gesture = await tester.startGesture(
      tester.getCenter(find.byType(UiMixSlider)),
    );
    await gesture.moveBy(const Offset(40, 0));
    await tester.pump();
    await gesture.moveBy(const Offset(40, 0));
    await tester.pump();
    expect(sent, isEmpty);

    await gesture.up();
    await tester.pumpAndSettle();

    expect(sent, hasLength(1));
    expect(sent.single['signal'], 'control.setValue');
    expect(sent.single['target'], 'depth');
    expect(sent.single['args']['value'], greaterThan(5));
  });

  testWidgets('props that change while the person drags are ignored', (
    tester,
  ) async {
    await _pump(tester, {..._props, 'value': 1});

    final gesture = await tester.startGesture(
      tester.getCenter(find.byType(UiMixSlider)),
    );
    await gesture.moveBy(const Offset(40, 0));
    await tester.pump();
    final dragged = tester.widget<UiMixSlider>(find.byType(UiMixSlider)).value;

    tester.state<_HostState>(find.byType(_Host)).render({
      ..._props,
      'value': 2,
    });
    await tester.pump();

    expect(tester.widget<UiMixSlider>(find.byType(UiMixSlider)).value, dragged);
    await gesture.up();
    await tester.pumpAndSettle();
  });

  testWidgets('a slider with disabled props sends nothing', (tester) async {
    final sent = await _pump(tester, {..._props, 'isEnabled': false});

    await tester.drag(find.byType(UiMixSlider), const Offset(80, 0));
    await tester.pumpAndSettle();

    expect(sent, isEmpty);
  });
}
