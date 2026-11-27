import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

// A 1x1 transparent PNG.
const _png =
    'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';

Future<List<Map<String, dynamic>>> _pump(
  WidgetTester tester,
  Map<String, dynamic> json,
) async {
  final sent = <Map<String, dynamic>>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.intochat(Brightness.light),
      home: Scaffold(
        body: SingleChildScrollView(
          child: NodeRenderer.render(
            Node.fromJson(json),
            NodeContext(
              view: 'local/apps/files/view',
              generation: 1,
              actNeuron: 'local/apps/files',
              send: (event) async {
                sent.add(event.toJson());
                return {'delivered': true};
              },
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  return sent;
}

Map<String, dynamic> _image({String url = _png, String caption = 'Preview'}) =>
    {
      'kind': 'image',
      'url': url,
      'caption': caption,
      'height': 200,
      'fit': 'Contain',
    };

void main() {
  testWidgets('an image node draws its caption and the picture', (
    tester,
  ) async {
    await _pump(tester, _image());

    expect(find.text('Preview'), findsOneWidget);
    expect(find.byType(Image), findsOneWidget);
    expect(find.text('100% · 0°'), findsOneWidget);
  });

  testWidgets('zoom and rotation stay in the client: nothing is sent', (
    tester,
  ) async {
    final sent = await _pump(tester, _image());

    await tester.tap(find.byKey(const ValueKey('image:zoomIn')));
    await tester.pump();
    expect(find.text('125% · 0°'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('image:rotate')));
    await tester.pump();
    expect(find.text('125% · 90°'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('image:zoomOut')));
    await tester.tap(find.byKey(const ValueKey('image:zoomOut')));
    await tester.pump();
    expect(find.text('75% · 90°'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('image:reset')));
    await tester.pump();
    expect(find.text('100% · 0°'), findsOneWidget);

    expect(sent, isEmpty);
  });

  testWidgets('zoom stops at its bounds', (tester) async {
    await _pump(tester, _image());

    for (var i = 0; i < 20; i++) {
      await tester.tap(find.byKey(const ValueKey('image:zoomIn')));
    }
    await tester.pump();
    expect(find.text('400% · 0°'), findsOneWidget);

    for (var i = 0; i < 20; i++) {
      await tester.tap(find.byKey(const ValueKey('image:zoomOut')));
    }
    await tester.pump();
    expect(find.text('25% · 0°'), findsOneWidget);
  });

  testWidgets('an image with no source says so and has no tools', (
    tester,
  ) async {
    await _pump(tester, _image(url: ''));

    expect(find.text('No image preview available'), findsOneWidget);
    expect(find.byKey(const ValueKey('image:zoomIn')), findsNothing);
  });
}
