import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  testWidgets('neuronViewRegistersKindName', (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: ViewHost(
            knowsApp: (_) => false,
            kind: 'view',
            name: 'test',
            watch: (_, _) => const Stream<void>.empty(),
            load: (_, _) async => const PublishedViewPayload(),
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
          knowsApp: (_) => false,
          kind: 'view',
          name: name,
          watch: (_, _) => const Stream<void>.empty(),
          load: (_, _) async => const PublishedViewPayload(),
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
