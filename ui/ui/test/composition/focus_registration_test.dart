import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

void main() {
  testWidgets('neuronViewRegistersKindName', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
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

  testWidgets('neuronViewReRegistersWhenNameChanges', (tester) async {
    Widget view(String name) => MaterialApp(
      home: Scaffold(
        body: ViewHost(
          kind: 'view',
          name: name,
          watch: (_, _) => const Stream<void>.empty(),
          load: (_, _) async => {'version': 1, 'markdown': 'hello'},
        ),
      ),
    );
    await tester.pumpWidget(view('one'));
    await tester.pump();
    expect(FocusRegistry.instance.resolve('view:one'), isNotNull);
    await tester.pumpWidget(view('two'));
    await tester.pump();
    expect(FocusRegistry.instance.resolve('view:one'), isNull);
    expect(FocusRegistry.instance.resolve('view:two'), isNotNull);
    await tester.pumpWidget(const SizedBox());
  });
}
