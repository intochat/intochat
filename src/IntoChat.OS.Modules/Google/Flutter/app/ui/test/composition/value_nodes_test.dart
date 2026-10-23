import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

Map<String, dynamic> _button(
  String key,
  String text, {
  int style = 0,
  String? icon,
}) => {
  'kind': 1,
  'key': key,
  'text': text,
  'action': key,
  'style': style,
  'icon': icon,
};

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> root,
) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      home: Scaffold(
        body: SizedBox(
          width: 800,
          height: 600,
          child: NeuronView(
            kind: 'view',
            name: 'nodes',
            load: (kind, name) async => {
              'generation': 1,
              'version': 1,
              'root': root,
            },
            watch: (kind, name) => const Stream.empty(),
            onAction: (event) async => sent.add(event),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
  return sent;
}

Map<String, dynamic> _menu() => {
  'kind': 6,
  'key': 'menu',
  'text': 'More',
  'icon': 'more-horizontal',
  'children': [
    {'kind': 0, 'key': 'label', 'text': 'Chat'},
    _button('rename', 'Rename'),
    {'kind': 12, 'key': 'sep'},
    _button('archive', 'Archive', icon: 'archive'),
  ],
};

void main() {
  testWidgets('tapping a menu item dispatches its action once', (tester) async {
    final sent = await _pump(tester, _menu());
    await tester.tap(find.bySemanticsLabel('More'));
    await tester.pumpAndSettle();
    expect(find.text('CHAT'), findsOneWidget);
    await tester.tap(find.text('Archive'));
    await tester.pumpAndSettle();
    expect(sent, hasLength(1));
    expect(sent.single, containsPair('action', 'archive'));
    expect(sent.single, containsPair('kind', 'view'));
    expect(sent.single, containsPair('field', 'archive'));
    expect(find.text('Archive'), findsNothing);
  });

  testWidgets('Escape on an open menu dispatches nothing', (tester) async {
    final sent = await _pump(tester, _menu());
    await tester.tap(find.bySemanticsLabel('More'));
    await tester.pumpAndSettle();
    expect(find.text('Rename'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(find.text('Rename'), findsNothing);
    expect(sent, isEmpty);
  });

  testWidgets('modal Escape dispatches the cancel action', (tester) async {
    final sent = await _pump(tester, {
      'kind': 7,
      'key': 'confirm',
      'text': 'Delete chat?',
      'detail': 'This cannot be undone.',
      'children': [
        _button('delete', 'Delete', style: 3),
        _button('cancel', 'Cancel', style: 1),
      ],
    });
    await tester.pumpAndSettle();
    expect(find.text('Delete chat?'), findsOneWidget);
    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();
    expect(sent.single, containsPair('action', 'cancel'));
  });

  testWidgets('a toast is gone after four seconds', (tester) async {
    await _pump(tester, {
      'kind': 8,
      'key': 'toast',
      'text': 'Chat archived',
      'children': [_button('undo', 'Undo')],
    });
    expect(find.text('Chat archived'), findsOneWidget);
    expect(find.text('Undo'), findsOneWidget);
    await tester.pump(const Duration(seconds: 4));
    await tester.pump();
    expect(find.text('Chat archived'), findsNothing);
    expect(find.text('Undo'), findsNothing);
  });

  testWidgets('a 500-row list builds lazily', (tester) async {
    await _pump(tester, {
      'kind': 9,
      'key': 'list',
      'children': [
        for (var i = 0; i < 500; i++)
          {'kind': 0, 'key': 'r$i', 'text': 'Row $i'},
      ],
    });
    expect(find.textContaining('Row ').evaluate().length, lessThan(50));
    expect(find.text('Row 0'), findsOneWidget);
  });

  testWidgets('unknown kind falls back and siblings still render', (
    tester,
  ) async {
    await _pump(tester, {
      'kind': 2,
      'key': 'root',
      'children': [
        {'kind': 99, 'key': 'odd'},
        {'kind': 0, 'key': 'ok', 'text': 'Still here'},
      ],
    });
    expect(find.text('This component cannot be displayed.'), findsOneWidget);
    expect(find.textContaining('Still here'), findsOneWidget);
  });

  testWidgets('an icon button exposes its text as semantics label', (
    tester,
  ) async {
    final handle = tester.ensureSemantics();
    final sent = await _pump(
      tester,
      _button('close', 'Close', style: 2, icon: 'x'),
    );
    expect(find.bySemanticsLabel('Close'), findsOneWidget);
    expect(find.text('Close'), findsNothing);
    await tester.tap(find.bySemanticsLabel('Close'));
    await tester.pump();
    expect(sent.single, containsPair('action', 'close'));
    handle.dispose();
  });

  testWidgets('badge, avatar and divider render their text', (tester) async {
    await _pump(tester, {
      'kind': 2,
      'key': 'root',
      'children': [
        {'kind': 10, 'key': 'b', 'text': 'Done', 'tone': 1},
        {'kind': 11, 'key': 'a', 'text': 'VH'},
        {'kind': 12, 'key': 'd', 'text': 'or'},
      ],
    });
    expect(find.text('Done'), findsOneWidget);
    expect(find.text('VH'), findsOneWidget);
    expect(find.text('or'), findsOneWidget);
  });
}
