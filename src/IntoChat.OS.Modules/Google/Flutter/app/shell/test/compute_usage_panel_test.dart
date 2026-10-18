import 'dart:convert';

import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/workspace/compute_usage_panel.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';

final _recentCosts = [
  {
    'traceId': 'trace-1',
    'costId': 'cost-1',
    'kind': 'model',
    'item': 'gpt-5.4:input',
    'quantity': 0.0012,
    'unit': 'mtok',
    'micros': 1500,
    'synapse': null,
    'app': null,
    'neuron': null,
    'at': '2026-10-10T12:00:00Z',
  },
];

Widget panelWith(IntoChatOSUiClient client, {Listenable? refreshSignal}) =>
    MaterialApp(
      home: Scaffold(
        body: ComputeUsagePanel(
          client: client,
          workspaceId: 'workspace',
          workspaceName: 'My project',
          onClose: () {},
          refreshSignal: refreshSignal,
        ),
      ),
    );

void main() {
  testWidgets('panel renders balance, spend, grouped rows and recent costs', (
    tester,
  ) async {
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        if (request.url.path.endsWith('/recent')) {
          return http.Response(jsonEncode(_recentCosts), 200);
        }
        return http.Response(
          jsonEncode({
            'balanceMicros': 3500000,
            'spentMicros': 1500,
            'rows': [
              {'key': 'brain/apps/x/synapses/a.cs', 'micros': 1200, 'count': 3},
              {'key': 'brain/apps/y', 'micros': 300, 'count': 1},
            ],
          }),
          200,
        );
      }),
    );
    await tester.pumpWidget(panelWith(client));
    await tester.pumpAndSettle();
    expect(find.text('Balance'), findsOneWidget);
    expect(find.text('\$3.5'), findsOneWidget);
    expect(find.text('Spent · last 30 days'), findsOneWidget);
    expect(find.text('\$0.0015'), findsNWidgets(2));
    expect(find.text('Spend by app'), findsOneWidget);
    expect(find.text('brain/apps/x/synapses/a.cs'), findsOneWidget);
    expect(find.text('3 calls'), findsOneWidget);
    expect(find.text('\$0.0012'), findsOneWidget);
    expect(find.text('Recent'), findsOneWidget);
    expect(find.textContaining('gpt-5.4:input'), findsOneWidget);
    expect(find.textContaining('· model'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('switching grouping to Synapse re-reads compute by synapse', (
    tester,
  ) async {
    final bys = <String>[];
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        if (request.url.path.endsWith('/recent')) {
          return http.Response(jsonEncode(<Object>[]), 200);
        }
        final by = request.url.queryParameters['by'];
        bys.add(by!);
        return http.Response(
          jsonEncode({
            'balanceMicros': 0,
            'spentMicros': 900,
            'rows': by == 'synapse'
                ? [
                    {
                      'key': 'brain/apps/x/synapses/a.cs',
                      'micros': 900,
                      'count': 2,
                    },
                  ]
                : [
                    {'key': 'brain/apps/x', 'micros': 900, 'count': 2},
                  ],
          }),
          200,
        );
      }),
    );
    await tester.pumpWidget(panelWith(client));
    await tester.pumpAndSettle();
    expect(find.text('brain/apps/x'), findsOneWidget);
    expect(find.text('brain/apps/x/synapses/a.cs'), findsNothing);
    await tester.tap(find.byKey(const Key('compute-group-synapse')));
    await tester.pumpAndSettle();
    expect(bys, ['app', 'synapse']);
    expect(find.text('Spend by synapse'), findsOneWidget);
    expect(find.text('brain/apps/x/synapses/a.cs'), findsOneWidget);
    expect(find.text('brain/apps/x'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('refresh signal re-reads grouped totals', (tester) async {
    var ready = false;
    final signal = ValueNotifier<int>(0);
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        if (request.url.path.endsWith('/recent')) {
          return http.Response(jsonEncode(<Object>[]), 200);
        }
        return http.Response(
          jsonEncode({
            'balanceMicros': 0,
            'spentMicros': ready ? 200 : 0,
            'rows': ready
                ? [
                    {'key': 'brain/apps/x', 'micros': 200, 'count': 1},
                  ]
                : <Object>[],
          }),
          200,
        );
      }),
    );
    await tester.pumpWidget(panelWith(client, refreshSignal: signal));
    await tester.pumpAndSettle();
    expect(find.text('No usage recorded yet.'), findsOneWidget);
    ready = true;
    signal.value++;
    await tester.pumpAndSettle();
    expect(find.text('brain/apps/x'), findsOneWidget);
    expect(find.text('\$0.0002'), findsNWidgets(2));
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
    signal.dispose();
  });

  testWidgets('fetch failure surfaces an error and refresh recovers', (
    tester,
  ) async {
    var fail = true;
    final client = IntoChatOSUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: MockClient((request) async {
        if (request.url.path.endsWith('/recent')) {
          return http.Response(jsonEncode(<Object>[]), 200);
        }
        if (fail) return http.Response('{"error":"offline"}', 503);
        return http.Response(
          jsonEncode({
            'balanceMicros': 0,
            'spentMicros': 400,
            'rows': [
              {'key': 'brain/apps/x', 'micros': 400, 'count': 1},
            ],
          }),
          200,
        );
      }),
    );
    await tester.pumpWidget(panelWith(client));
    await tester.pumpAndSettle();
    expect(find.textContaining('Could not load usage'), findsOneWidget);
    fail = false;
    await tester.tap(find.byTooltip('Refresh usage'));
    await tester.pumpAndSettle();
    expect(find.text('brain/apps/x'), findsOneWidget);
    expect(find.textContaining('Could not load usage'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });
}
