import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

void main() {
  tearDown(() {
    FocusRegistry.instance.unregister('a');
    FocusRegistry.instance.unregister('b');
  });

  testWidgets('registeredTargetResolvesToItsRect', (tester) async {
    final key = GlobalKey();
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Align(
          alignment: Alignment.topLeft,
          child: Padding(
            padding: const EdgeInsets.only(left: 10, top: 20),
            child: SizedBox(key: key, width: 30, height: 40),
          ),
        ),
      ),
    );
    FocusRegistry.instance.register('a', key);
    expect(
      FocusRegistry.instance.resolve('a'),
      const Rect.fromLTWH(10, 20, 30, 40),
    );
  });

  test('unregisteredReturnsNull', () {
    expect(FocusRegistry.instance.resolve('missing'), isNull);
  });

  testWidgets('disposeUnregisters', (tester) async {
    final key = GlobalKey();
    await tester.pumpWidget(SizedBox(key: key, width: 5, height: 5));
    FocusRegistry.instance.register('a', key);
    expect(FocusRegistry.instance.resolve('a'), isNotNull);
    FocusRegistry.instance.unregister('a');
    expect(FocusRegistry.instance.resolve('a'), isNull);
    // A key whose widget left the tree resolves to null too.
    FocusRegistry.instance.register('b', key);
    await tester.pumpWidget(const SizedBox());
    expect(FocusRegistry.instance.resolve('b'), isNull);
  });

  testWidgets('reRegisterReplacesKey', (tester) async {
    final first = GlobalKey(), second = GlobalKey();
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Column(
          children: [
            SizedBox(key: first, width: 10, height: 10),
            SizedBox(key: second, width: 20, height: 20),
          ],
        ),
      ),
    );
    FocusRegistry.instance.register('a', first);
    final before = FocusRegistry.instance.resolve('a');
    FocusRegistry.instance.register('a', second);
    final after = FocusRegistry.instance.resolve('a');
    expect(before!.size, const Size(10, 10));
    expect(after!.size, const Size(20, 20));
  });

  testWidgets('guardedUnregisterKeepsNewerKey', (tester) async {
    final k1 = GlobalKey(), k2 = GlobalKey();
    await tester.pumpWidget(
      Directionality(
        textDirection: TextDirection.ltr,
        child: Column(
          children: [
            SizedBox(key: k1, width: 10, height: 10),
            SizedBox(key: k2, width: 20, height: 20),
          ],
        ),
      ),
    );
    FocusRegistry.instance.register('a', k1);
    FocusRegistry.instance.register('a', k2);
    FocusRegistry.instance.unregister('a', k1);
    expect(FocusRegistry.instance.resolve('a')!.size, const Size(20, 20));
  });
}
