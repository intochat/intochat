import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';

const _view = 'local/apps/workspace/view';

PublishedViewPayload _published(Map<String, dynamic> root) =>
    PublishedViewPayload.fromJson({
      'generation': 5,
      'view': {'title': 'Workspace', 'root': root},
    });

Future<void> _host(
  WidgetTester tester,
  Map<String, dynamic> root, {
  List<Map<String, dynamic>>? events,
}) async {
  await tester.pumpWidget(
    MaterialApp(
      theme: UiTheme.light('intochat'),
      home: Scaffold(
        body: ViewHost(
          knowsApp: (_) => false,
          kind: 'view',
          name: _view,
          load: (kind, name) async => _published(root),
          watch: (kind, name) => const Stream.empty(),
          onAction: (event) async {
            events?.add(event.toJson());
            return null;
          },
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
    'metricTile renders KPI value, delta, status pill, sparkline with threshold, and actions',
    (tester) async {
      final events = <Map<String, dynamic>>[];
      await _host(tester, {
        'kind': 'metricTile',
        'id': 'btcalert',
        'appId': 'workspace',
        'title': 'BTC / USD',
        'value': r'$84,120.50',
        'delta': '-1.8%',
        'deltaTone': 'error',
        'statusLabel': 'Triggered',
        'statusTone': 'working',
        'sparkline': [86200.0, 85900.0, 85120.0, 84120.5],
        'threshold': 85000.0,
        'actions': [
          {
            'key': 'reset-alert',
            'label': 'Reset alert',
            'command': {'target': '.', 'signal': 'reset'},
          },
        ],
      }, events: events);

      expect(find.text('BTC / USD'), findsOneWidget);
      expect(find.text(r'$84,120.50'), findsOneWidget);
      expect(find.text('-1.8%'), findsOneWidget);
      expect(find.text('Triggered'), findsOneWidget);
      expect(find.text('Threshold 85000'), findsOneWidget);

      final sparklinePaint = tester.widget<CustomPaint>(
        find.byKey(const ValueKey('sparkline:btcalert')),
      );
      expect(sparklinePaint.painter, isA<SparklinePainter>());
      final painter = sparklinePaint.painter! as SparklinePainter;
      expect(painter.points, [86200.0, 85900.0, 85120.0, 84120.5]);
      expect(painter.threshold, 85000.0);

      await tester.tap(find.text('Reset alert'));
      await tester.pumpAndSettle();
      expect(events.single['signal'], 'reset');
    },
  );
}
