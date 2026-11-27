import 'package:intochat_os_flutter_shell/intochat_os_ui.dart'
    show kitToastLifetime;

import 'dart:async' show unawaited;
import 'dart:convert' show jsonDecode;

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:flutter/widgets.dart' show Size;
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';

import 'application_fixture.dart';

void main() {
  for (final target in ['brain-1', 'circle-test']) {
    testWidgets('root routes $target activation to its app-owned navigator', (
      tester,
    ) async {
      final server = ActivationServer(target);
      final fixture = await ApplicationFixture.start(tester, server: server);
      unawaited(
        fixture.appState.activateAction({
          'brainId': target,
          'application': 'target',
          'operation': 'profile',
          'input': 'person-1',
        }),
      );
      await tester.pumpAndSettle();
      final requests = server.requestsTo(
        '/brains/$target/apps/navigation/request',
      );
      expect(requests, hasLength(1));
      expect(requests.single.body['operation'], 'activate');
      expect(jsonDecode(requests.single.body['input'] as String), {
        'brainId': target,
        'application': 'target',
        'operation': 'profile',
        'input': 'person-1',
      });
      expect(
        server.requests.where(
          (r) =>
              r.path.endsWith('/show') ||
              r.path.endsWith('/apps/target/request'),
        ),
        isEmpty,
      );
      await fixture.dispose();
    });
  }

  testWidgets(
    'server-selected root mounts without legacy workspace navigation',
    (tester) async {
      final server = ApplicationServer()
        ..root = {'kind': 'view', 'name': 'brain-1/apps/assistant/surface'};
      final fixture = await ApplicationFixture.start(tester, server: server);
      expect(find.byType(LiveDesktopShell), findsNothing);
      expect(find.text('Ask the assistant'), findsOneWidget);
      expect(server.requestsTo('/brains/brain-1/apps/connect'), hasLength(1));
      expect(
        server.requests.where(
          (r) => r.path.endsWith('/show') || r.path.endsWith('/request'),
        ),
        isEmpty,
      );
      await fixture.dispose();
    },
  );

  testWidgets('server-selected root watches its nodes and never polls', (
    tester,
  ) async {
    final server = ApplicationServer()
      ..root = {'kind': 'view', 'name': 'brain-1/apps/assistant/surface'};
    final fixture = await ApplicationFixture.start(tester, server: server);
    await tester.pump(const Duration(milliseconds: 100));
    final watched = {
      for (final w in fixture.link.watches)
        for (final s in w.$2) s.key,
    };
    expect(watched, contains('brain-1/apps/assistant/surface'));
    // A field's text travels with its command: no field neuron is watched.
    expect(watched, isNot(contains('brain-1/apps/assistant/draft')));
    int reads() => server.requestsTo('/brains/brain-1/apps/node').length;
    final settled = reads();
    // Ten former poll periods: no node is read again without a change signal.
    await tester.pump(const Duration(seconds: 20));
    expect(reads(), settled);
    fixture.link.events.add(
      const NeuronSignal(
        source: 'brain-1/apps/assistant/surface',
        type: 'FlutterViewChanged',
        publisher: 'brain-1/apps/assistant/surface',
        sequence: 0,
        payload: {},
        serverTimeMs: 0,
      ),
    );
    await tester.pump();
    await tester.pump();
    expect(reads(), settled + 1);
    await fixture.dispose();
  });

  // The layout switch hands the shell {kind: reconnect}: it connects again and mounts the new root.
  testWidgets('reconnect activation connects again and mounts the new root', (
    tester,
  ) async {
    final server = ReconnectServer()
      ..root = {'kind': 'view', 'name': 'brain-1/apps/assistant/surface'};
    final fixture = await ApplicationFixture.start(tester, server: server);
    unawaited(fixture.appState.activateAction(_classicLayout));
    await tester.pumpAndSettle();
    expect(server.connects, 2);
    final switched = server.requestsTo('/brains/brain-1/apps/desktop/request');
    expect(switched.single.body['operation'], 'layout');
    expect(switched.single.body['input'], 'classic');
    expect(find.text('Ask the assistant'), findsOneWidget);
    expect(
      server.requestsTo('/brains/brain-1/apps/navigation/request'),
      isEmpty,
    );
    await tester.pump(kitToastLifetime);
    await fixture.dispose();
  });

  // The desktop root learns the window's width: narrow below 900 logical px, again on each crossing.
  testWidgets(
    'desktop root sends its narrow width hint on mount and on crossing',
    (tester) async {
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = const Size(1280, 800);
      addTearDown(tester.view.reset);
      final server = DesktopServer()
        ..root = {'kind': 'view', 'name': 'brain-1/apps/desktop/view'};
      final fixture = await ApplicationFixture.start(tester, server: server);
      List<String?> hints() => [
        for (final r in server.requestsTo(
          '/brains/brain-1/apps/desktop/request',
        ))
          if (r.body['operation'] == 'narrow') r.body['input'] as String?,
      ];
      expect(hints(), ['false']);
      tester.view.physicalSize = const Size(700, 800);
      await tester.pumpAndSettle();
      expect(hints(), ['false', 'true']);
      await fixture.dispose();
    },
  );

  testWidgets('other roots get no width hint', (tester) async {
    final server = ApplicationServer()
      ..root = {'kind': 'view', 'name': 'brain-1/apps/assistant/surface'};
    final fixture = await ApplicationFixture.start(tester, server: server);
    expect(server.requestsTo('/brains/brain-1/apps/desktop/request'), isEmpty);
    await fixture.dispose();
  });

  testWidgets('reconnect that never changes the root says when it will', (
    tester,
  ) async {
    final server = ReconnectServer(switches: false)
      ..root = {'kind': 'view', 'name': 'brain-1/apps/assistant/surface'};
    final fixture = await ApplicationFixture.start(tester, server: server);
    unawaited(fixture.appState.activateAction(_classicLayout));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 300));
    }
    await tester.pump();
    expect(server.connects, 11);
    expect(find.text('Layout will change on next start'), findsOneWidget);
    await tester.pump(kitToastLifetime);
    await fixture.dispose();
  });
}

const _classicLayout = {
  'kind': 'reconnect',
  'brainId': 'brain-1',
  'application': 'desktop',
  'operation': 'layout',
  'input': 'classic',
};

class ActivationServer extends ApplicationServer {
  ActivationServer(this.targetBrain);
  final String targetBrain;
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (request.url.path.endsWith('/apps/connect')) {
      requests.add(RecordedAppRequest(request.url.path, body(request)));
      final brain = request.url.pathSegments[1];
      return json({
        'root': {'kind': 'view', 'name': '$brain/apps/assistant/surface'},
        'activationApp': 'navigation',
      });
    }
    return super.send(request);
  }
}

class ReconnectServer extends ApplicationServer {
  ReconnectServer({this.switches = true});
  final bool switches;
  int connects = 0;
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (request.url.path.endsWith('/apps/connect')) {
      connects++;
      if (connects == 1 || !switches) {
        return json({
          'root': {'kind': 'view', 'name': 'brain-1/apps/assistant/surface'},
          'activationApp': 'navigation',
        });
      }
    }
    return super.send(request);
  }
}

class DesktopServer extends ApplicationServer {
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    if (request.url.path.endsWith('/apps/desktop/projection')) {
      return json({
        'windows': const [],
        'focusedViewId': null,
        'pins': const [],
        'tiles': const [],
        'openWidgets': const [],
        'stylePreset': 'default',
        'barLayout': 'bottom',
        'layoutMode': 'smart-desktop',
        'isNarrow': false,
      });
    }
    if (request.url.path.endsWith('/apps/node')) {
      return json({
        'generation': 1,
        'root': {'key': 'desktop', 'kind': 'text', 'text': 'Desktop'},
      });
    }
    if (request.url.path.endsWith('/apps/desktop/request')) {
      requests.add(RecordedAppRequest(request.url.path, body(request)));
      return json({'requestId': 'r'});
    }
    return super.send(request);
  }
}
