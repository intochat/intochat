// Wire JSON shaped like features/os/Desktop/DesktopLayout.cs and
// features/os/Ino/InoView.cs; references become text so no child loads run.

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

Map<String, dynamic> shapeButton(String key, String text) => {
  'kind': 1,
  'key': key,
  'text': text,
  'action': key,
  'style': 1,
};

Map<String, dynamic> _text(String key, String text) => {
  'kind': 0,
  'key': key,
  'text': text,
};

Map<String, dynamic> _row(
  String key,
  List<Map<String, dynamic>> children, {
  List<num>? extents,
  num gap = 0,
}) => {
  'kind': 3,
  'key': key,
  'children': children,
  'extents': ?extents,
  'gap': gap,
};

Map<String, dynamic> _column(
  String key,
  List<Map<String, dynamic>> children, {
  List<num>? extents,
  num gap = 0,
}) => {
  'kind': 2,
  'key': key,
  'children': children,
  'extents': ?extents,
  'gap': gap,
};

/// Ino's main view: a 40 px header row whose button has a 160 px slot.
Map<String, dynamic> inoShape() => _column(
  'main',
  [
    _row(
      'header',
      [_text('status', 'Ino is ready'), shapeButton('new', 'New conversation')],
      extents: [0, 160],
    ),
    _text('transcript', 'Transcript'),
    _text('composer', 'Composer'),
  ],
  extents: [40, 0, 152],
);

Map<String, dynamic> _window(String id, String title, {bool pin = true}) =>
    _column(
      'frame-$id',
      [
        {
          'kind': 4,
          'key': 'window-$id',
          'text': title,
          'children': [
            _row('chrome-$id', [
              shapeButton('focus-$id', 'Focus'),
              if (pin) shapeButton('pin-$id', 'Pin'),
              shapeButton('close-$id', 'Close'),
            ], gap: 4),
          ],
        },
        id == 'ino' ? inoShape() : _text('content-$id', '$title content'),
      ],
      gap: 4,
      extents: [112, 0],
    );

/// The desktop: one row of windows above the 68 px dock.
Map<String, dynamic> desktopShape() => _column(
  'desktop',
  [
    _column(
      'windows',
      [
        _row('windows-0', [
          _window('ino', 'Ino', pin: false),
          _window('files', 'Files'),
        ]),
      ],
      extents: [0],
    ),
    _row(
      'dock',
      [
        _row('apps', [
          shapeButton('dock-ino', 'Ino'),
          shapeButton('dock-files', 'Files'),
          shapeButton('gallery', 'Apps'),
        ], gap: 4),
        _row(
          'right',
          [
            _text('usage', 'Usage'),
            shapeButton('avatar', 'Vlad'),
            _row('more', [
              shapeButton('more-settings', 'Settings'),
              shapeButton('more-classic', 'Classic layout'),
            ], gap: 4),
          ],
          gap: 4,
          extents: [72, 72, 0],
        ),
      ],
      extents: [0, 360],
    ),
  ],
  extents: [0, 68],
);

/// Pumps [root] as a view value at [size].
Future<void> pumpShape(
  WidgetTester tester,
  Map<String, dynamic> root, {
  ThemeData? theme,
  Size size = const Size(1280, 800),
}) async {
  await tester.pumpWidget(
    MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: theme,
      home: Material(
        child: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: size.width,
            height: size.height,
            child: NeuronView(
              kind: 'view',
              name: 'desktop',
              load: (kind, name) async => {
                'generation': 1,
                'version': 1,
                'root': root,
              },
              watch: (kind, name) => const Stream.empty(),
              onAction: (event) async {},
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
}
