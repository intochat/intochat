import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

void main() {
  testWidgets('neuronViewRegistersKindName', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: NeuronView(
            kind: 'view',
            name: 'test',
            watch: (_, _) => const Stream<void>.empty(),
            load: (_, _) async => {'version': 1, 'markdown': 'hello'},
          ),
        ),
      ),
    );
    await tester.pump();
    expect(FocusRegistry.instance.resolve('view:test'), isNotNull);
    await tester.pumpWidget(const SizedBox());
    expect(FocusRegistry.instance.resolve('view:test'), isNull);
  });
}
