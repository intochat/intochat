import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/dock/assistant_island.dart';
import 'package:digitalbrain_flutter_shell/workspace/dock/dock.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

const player = AppManifest(
  title: 'Player',
  description: 'plays',
  operations: ['play', 'mute', 'volume', 'say'],
  toolbar: [
    AppToolbarEntry(operation: 'play', icon: 'play', label: 'Play'),
    AppToolbarEntry(
      operation: 'mute',
      kind: AppToolbarKind.toggle,
      label: 'Mute',
    ),
    AppToolbarEntry(
      operation: 'volume',
      kind: AppToolbarKind.slider,
      min: 0,
      max: 100,
    ),
    AppToolbarEntry(operation: 'say', kind: AppToolbarKind.text, label: 'Say'),
  ],
);

const assistant = AppManifest(
  title: 'Assistant',
  description: '',
  operations: ['open', 'send', 'new', 'voice'],
  toolbar: [
    AppToolbarEntry(
      operation: 'send',
      kind: AppToolbarKind.text,
      label: 'Message your brain',
    ),
    AppToolbarEntry(operation: 'voice', icon: 'mic', label: 'Voice'),
  ],
);

Widget host(String app, AppManifest manifest, List<(String, String)> posted) =>
    MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: Dock(
            app: app,
            manifest: manifest,
            onRequest: (operation, input) async =>
                posted.add((operation, input)),
          ),
        ),
      ),
    );

void main() {
  testWidgets('each kind posts its operation with the input the spec names', (
    tester,
  ) async {
    final posted = <(String, String)>[];
    await tester.pumpWidget(host('player', player, posted));

    await tester.tap(find.byKey(const ValueKey('dock-play')));
    await tester.tap(find.byKey(const ValueKey('dock-mute')));
    await tester.pump();
    expect(posted, [('play', ''), ('mute', 'on')]);

    await tester.drag(
      find.byKey(const ValueKey('dock-volume')),
      const Offset(60, 0),
    );
    await tester.pumpAndSettle();
    expect(posted, hasLength(3));
    expect(posted[2].$1, 'volume');
    expect(double.parse(posted[2].$2), greaterThan(0));

    await tester.enterText(find.byKey(const ValueKey('dock-say')), 'hello');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pump();
    expect(posted.last, ('say', 'hello'));
    expect(
      tester.widget<TextField>(find.byType(TextField)).controller!.text,
      '',
    );
    expect(find.byKey(const ValueKey('assistant-ino')), findsNothing);
  });

  testWidgets('an empty submit posts nothing', (tester) async {
    final posted = <(String, String)>[];
    await tester.pumpWidget(host('player', player, posted));
    await tester.enterText(find.byKey(const ValueKey('dock-say')), '   ');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pump();
    expect(posted, isEmpty);
  });

  testWidgets(
    'the assistant toolbar is the assistant island: Ino, then its entries',
    (tester) async {
      final posted = <(String, String)>[];
      await tester.pumpWidget(host('assistant', assistant, posted));

      expect(find.byKey(const ValueKey('assistant-ino')), findsOneWidget);
      expect(find.byType(DockTextField), findsOneWidget);
      expect(find.byIcon(Icons.mic_rounded), findsOneWidget);
      final ino = tester.getTopLeft(
        find.byKey(const ValueKey('assistant-ino')),
      );
      final field = tester.getTopLeft(find.byType(DockTextField));
      final mic = tester.getTopLeft(find.byKey(const ValueKey('dock-voice')));
      expect(ino.dx, lessThan(field.dx));
      expect(field.dx, lessThan(mic.dx));

      await tester.tap(find.byKey(const ValueKey('dock-voice')));
      expect(posted, [('voice', '')]);
    },
  );
}
