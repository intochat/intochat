import 'dart:convert' show jsonDecode, jsonEncode;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:intochat_os_flutter_shell/desktop/live_desktop_shell.dart';
import 'package:intochat_os_flutter_shell/desktop/window_frame.dart';
import 'package:intochat_os_flutter_shell/intochat_os_ui.dart';
import 'package:intochat_os_flutter_shell/workspace/cross_window_router.dart';

import '../application_fixture.dart';

void main() {
  group('OpenWindowRoute & departure rule parsing', () {
    test('parses app, input, argsJson, transition, and fromNeuron while dropping unknown keys', () {
      final argsJson = jsonEncode({
        'traceId': '4bf92f3577b34da6a3ce929d0e0e4736',
      });
      final uri =
          'os://window/open?app=aspire'
          '&args=${Uri.encodeQueryComponent(argsJson)}'
          '&transition=shared-axis-z'
          '&from=app.engineer'
          '&url=https://evil.example';

      final route = OpenWindowRoute.tryParse(uri);
      expect(route, isNotNull);
      expect(route!.app, 'aspire');
      expect(route.argsJson, argsJson);
      expect(route.transition, 'shared-axis-z');
      expect(route.fromNeuron, 'app.engineer');
      expect(route.hasNavigationPayload, isTrue);

      final plain = OpenWindowRoute.tryParse(
        'os://window/open?app=orleans&input=EngineerFlow',
        fallbackFromNeuron: 'brain-1/apps/engineer',
      );
      expect(plain, isNotNull);
      expect(plain!.app, 'orleans');
      expect(plain.input, 'EngineerFlow');
      expect(plain.transition, 'fade-through');
      expect(plain.fromNeuron, 'brain-1/apps/engineer');
      expect(plain.hasNavigationPayload, isTrue);

      expect(OpenWindowRoute.tryParse('https://evil.example/open'), isNull);
      expect(OpenWindowRoute.tryParse('os://window/open?app=../bad'), isNull);
    });

    test('distinguishes domain Rule rejections from fail-open unsupported handlers', () {
      expect(
        extractRuleRejectionMessage({
          'error': '. of brain-1/apps/engineer refused the command.',
          'reason': 'Rule:unsaved_draft: Save or discard draft changes before leaving.',
        }),
        'Save or discard draft changes before leaving.',
      );
      expect(
        extractRuleRejectionMessage({
          'error': "Screen 'app.legacy' has no [On] reaction for 'ui.navigate-from'.",
          'reason': 'HandlerFailed',
        }),
        isNull,
      );
      expect(
        extractRuleRejectionMessage({
          'error': 'brain-1/apps/legacy is not loaded.',
          'reason': 'NotLoaded',
        }),
        isNull,
      );
    });
  });

  testWidgets(
    'LiveDesktopShell opens/focuses target window and dispatches ui.navigate-to for input= and args= OpenWindow intents',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1440, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final actEvents = <Map<String, dynamic>>[];
      final activations = <Map<String, dynamic>>[];
      var aspireUrl = 'http://localhost:18888/resources';
      var orleansUrl = '/orleans#/overview';

      Map<String, dynamic> desktopTree() => {
        'generation': 1,
        'view': {
          'title': 'Desktop',
          'root': {
            'kind': 'column',
            'children': [
              {
                'kind': 'column',
                'children': [
                  {
                    'kind': 'row',
                    'children': [
                      {
                        'kind': 'button',
                        'key': 'focus-w1',
                        'label': 'IntoChat Engineer',
                        'command': {
                          'target': '.',
                          'signal': 'focus',
                          'args': 'w1',
                        },
                        'enabled': true,
                      },
                      {
                        'kind': 'button',
                        'key': 'pin-w1',
                        'label': 'Pin',
                        'command': {
                          'target': '.',
                          'signal': 'pin',
                          'args': 'engineer',
                        },
                        'enabled': true,
                      },
                    ],
                  },
                  {
                    'kind': 'neuronRef',
                    'refKind': 'viewport',
                    'neuronUri': 'brain-1/apps/desktop/window/w1',
                  },
                ],
              },
              {
                'kind': 'row',
                'children': [
                  {
                    'kind': 'button',
                    'key': 'dock-engineer',
                    'label': 'Engineer',
                    'command': {
                      'target': '.',
                      'signal': 'open',
                      'args': 'engineer',
                    },
                    'enabled': true,
                  },
                  {
                    'kind': 'button',
                    'key': 'dock-aspire',
                    'label': '.NET Aspire',
                    'command': {
                      'target': '.',
                      'signal': 'open',
                      'args': 'aspire',
                    },
                    'enabled': true,
                  },
                  {
                    'kind': 'button',
                    'key': 'dock-orleans',
                    'label': 'Orleans Dashboard',
                    'command': {
                      'target': '.',
                      'signal': 'open',
                      'args': 'orleans',
                    },
                    'enabled': true,
                  },
                ],
              },
            ],
          },
        },
      };

      final typedArgsJson = jsonEncode({
        'traceId': '00f067aa0ba902b7',
        'section': 'traces',
      });

      await tester.pumpWidget(
        MaterialApp(
          theme: UiTheme.dark('fluent-acrylic'),
          builder: (context, child) => IcTheme(
            colors: IcColors.forPreset(IcStylePreset.fluentAcrylic, dark: true),
            child: child!,
          ),
          home: Scaffold(
            body: LiveDesktopShell(
              knowsApp: (app) =>
                  const {'engineer', 'aspire', 'orleans'}.contains(app),
              brain: 'brain-1',
              name: 'brain-1/apps/desktop/view',
              load: (kind, name) async {
                if (kind == 'viewport') {
                  return {
                    'revision': 1,
                    'root': {
                      'kind': 'view',
                      'name': 'brain-1/apps/engineer/view',
                    },
                  };
                }
                if (name == 'brain-1/apps/desktop/view') {
                  return desktopTree();
                }
                if (name == 'brain-1/apps/engineer/view') {
                  return {
                    'generation': 1,
                    'view': {
                      'title': 'IntoChat Engineer',
                      'root': {
                        'kind': 'row',
                        'children': [
                          {
                            'kind': 'button',
                            'key': 'engineer:open-aspire-input',
                            'label': 'Aspire Trace Input',
                            'intent': {'intentKind': 'OpenWindow', 'arg': ''},
                            'enabled': true,
                          },
                          {
                            'kind': 'button',
                            'key': 'engineer:open-aspire-args',
                            'label': 'Aspire Typed Args',
                            'intent': {'intentKind': 'OpenWindow', 'arg': ''},
                            'enabled': true,
                          },
                          {
                            'kind': 'button',
                            'key': 'engineer:open-orleans',
                            'label': 'Orleans Grain',
                            'intent': {'intentKind': 'OpenWindow', 'arg': ''},
                            'enabled': true,
                          },
                        ],
                      },
                    },
                  };
                }
                if (name == 'brain-1/apps/aspire/view') {
                  return {
                    'generation': 2,
                    'view': {
                      'title': '.NET Aspire',
                      'root': {'kind': 'text', 'value': 'AspireURL:$aspireUrl'},
                    },
                  };
                }
                if (name == 'brain-1/apps/orleans/view') {
                  return {
                    'generation': 2,
                    'view': {
                      'title': 'Orleans Dashboard',
                      'root': {
                        'kind': 'text',
                        'value': 'OrleansURL:$orleansUrl',
                      },
                    },
                  };
                }
                return {
                  'generation': 1,
                  'view': {
                    'title': name,
                    'root': {'kind': 'text', 'value': name},
                  },
                };
              },
              watch: (_, _) => const Stream.empty(),
              onAction: (event) async {
                if (event['kind'] == 'act') {
                  actEvents.add(event);
                  final args = event['args'] as Map<String, Object?>? ?? {};
                  if (event['neuron'] == 'brain-1/apps/aspire' &&
                      event['signal'] == 'ui.navigate-to') {
                    if (args['argsJson'] is String) {
                      final decoded =
                          jsonDecode(args['argsJson'] as String) as Map;
                      aspireUrl =
                          'http://localhost:18888/traces/detail/${decoded['traceId']}';
                    } else if (args['input'] is String) {
                      aspireUrl =
                          'http://localhost:18888/traces/detail/${args['input']}';
                    }
                  }
                  if (event['neuron'] == 'brain-1/apps/orleans' &&
                      event['signal'] == 'ui.navigate-to') {
                    orleansUrl = '/orleans#/grains/${args['input']}';
                  }
                  return {'delivered': true};
                }
                return switch (event['key']) {
                  'engineer:open-aspire-input' => {
                    'intent': {
                      'intentKind': 'OpenWindow',
                      'arg': 'os://window/open?app=aspire&input=4bf92f3577b34da6a3ce929d0e0e4736',
                    },
                  },
                  'engineer:open-aspire-args' => {
                    'intent': {
                      'intentKind': 'OpenWindow',
                      'arg':
                          'os://window/open?app=aspire'
                          '&args=${Uri.encodeQueryComponent(typedArgsJson)}'
                          '&transition=shared-axis-z'
                          '&from=app.engineer',
                    },
                  },
                  'engineer:open-orleans' => {
                    'intent': {
                      'intentKind': 'OpenWindow',
                      'arg': 'os://window/open?app=orleans&input=EngineerFlow',
                    },
                  },
                  _ => {'delivered': true},
                };
              },
              onActivate: activations.add,
              stylePreset: IcStylePreset.fluentAcrylic,
              barLayout: BarLayout.floatingGlass,
              paletteOpen: false,
              onTogglePalette: () {},
              onSelectPreset: (_) {},
              onCycleBarLayout: () {},
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(WindowFrame), findsOneWidget);

      // 1. Click os://window/open?app=aspire&input=4bf92f3577b34da6a3ce929d0e0e4736
      await tester.tap(find.text('Aspire Trace Input'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(WindowFrame), findsNWidgets(2));
      final firstAspireAct = actEvents.singleWhere(
        (e) =>
            e['neuron'] == 'brain-1/apps/aspire' &&
            e['signal'] == 'ui.navigate-to',
      );
      final firstArgs = firstAspireAct['args'] as Map<String, Object?>;
      expect(firstArgs['input'], '4bf92f3577b34da6a3ce929d0e0e4736');
      expect(firstArgs['transition'], 'fade-through');
      expect(firstArgs['fromNeuron'], 'brain-1/apps/engineer');
      expect(
        find.text(
          'AspireURL:http://localhost:18888/traces/detail/4bf92f3577b34da6a3ce929d0e0e4736',
        ),
        findsOneWidget,
      );

      // 2. Focus Engineer via dock, then click typed Aspire button -> focuses already-open Aspire window & dispatches ui.navigate-to!
      await tester.tap(find.byKey(const ValueKey('dock-item-engineer')));
      await tester.pump();
      await tester.tap(find.text('Aspire Typed Args'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      final aspireActs = actEvents
          .where((e) => e['neuron'] == 'brain-1/apps/aspire')
          .toList();
      expect(aspireActs, hasLength(2));
      final typedArgs = aspireActs.last['args'] as Map<String, Object?>;
      expect(typedArgs['argsJson'], typedArgsJson);
      expect(typedArgs['transition'], 'shared-axis-z');
      expect(typedArgs['fromNeuron'], 'app.engineer');
      expect(
        find.text(
          'AspireURL:http://localhost:18888/traces/detail/00f067aa0ba902b7',
        ),
        findsOneWidget,
      );

      // 3. Focus Engineer via dock and click os://window/open?app=orleans&input=EngineerFlow
      await tester.tap(find.byKey(const ValueKey('dock-item-engineer')));
      await tester.pump();
      await tester.tap(find.text('Orleans Grain'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 50));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byType(WindowFrame), findsNWidgets(3));
      final orleansAct = actEvents.singleWhere(
        (e) =>
            e['neuron'] == 'brain-1/apps/orleans' &&
            e['signal'] == 'ui.navigate-to',
      );
      final orleansActArgs = orleansAct['args'] as Map<String, Object?>;
      expect(orleansActArgs['input'], 'EngineerFlow');
      expect(orleansActArgs['transition'], 'fade-through');
      expect(
        find.text('OrleansURL:/orleans#/grains/EngineerFlow'),
        findsOneWidget,
      );

      await tester.pumpWidget(const SizedBox());
    },
  );

  testWidgets(
    'WorkspaceApp single-surface mode enforces ui.navigate-from Rule rejections, fails open on unsupported handlers, and dispatches ui.navigate-to',
    (tester) async {
      final server = _SingleSurfaceCrossNavServer()
        ..blockDepartureWithRule = true;
      final fixture = await ApplicationFixture.start(tester, server: server);

      expect(find.text('Engineer Workbench Active'), findsOneWidget);

      // 1. Attempt cross-window switch while Engineer has a broken departure Rule -> blocked + banner/toast!
      await tester.tap(find.text('Open Aspire Trace'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));

      final blockedFrom = server.actCalls
          .where((c) => c['signal'] == 'ui.navigate-from')
          .toList();
      expect(blockedFrom, hasLength(1));
      expect(blockedFrom.single['neuron'], 'brain-1/apps/engineer');
      expect((blockedFrom.single['args'] as Map)['force'], isFalse);
      expect(
        (blockedFrom.single['args'] as Map)['toNeuron'],
        'brain-1/apps/aspire',
      );

      // Switch was blocked: Engineer stays mounted, ui.navigate-to was NOT sent, refusal banner is visible
      expect(
        server.actCalls.where((c) => c['signal'] == 'ui.navigate-to'),
        isEmpty,
      );
      expect(find.text('Engineer Workbench Active'), findsOneWidget);
      expect(
        find.byKey(const ValueKey('workspace-departure-refusal-banner')),
        findsOneWidget,
      );
      expect(
        find.textContaining(
          'Live verification is in progress; wait or force departure.',
        ),
        findsWidgets,
      );

      // Dismiss refusal banner, then simulate screen without ui.navigate-from handler (422 HandlerFailed) -> fail-open & deep-link Aspire!
      await tester.tap(
        find.byKey(const ValueKey('workspace-departure-refusal-dismiss')),
      );
      await tester.pump();
      expect(
        find.byKey(const ValueKey('workspace-departure-refusal-banner')),
        findsNothing,
      );

      server
        ..blockDepartureWithRule = false
        ..navigateFromUnsupported = true;

      await tester.tap(find.text('Open Aspire Trace'));
      await tester.pumpAndSettle();

      final navToCalls = server.actCalls
          .where((c) => c['signal'] == 'ui.navigate-to')
          .toList();
      expect(navToCalls, hasLength(1));
      expect(navToCalls.single['neuron'], 'brain-1/apps/aspire');
      final navToArgs = navToCalls.single['args'] as Map<String, dynamic>;
      expect(navToArgs['transition'], 'shared-axis-x');
      expect(navToArgs['fromNeuron'], 'brain-1/apps/engineer');
      expect(jsonDecode(navToArgs['argsJson'] as String), {
        'traceId': '4bf92f3577b34da6a3ce929d0e0e4736',
      });

      expect(
        find.byKey(const ValueKey('workspace-departure-refusal-banner')),
        findsNothing,
      );
      expect(
        find.text(
          'Aspire DeepLink: http://localhost:18888/traces/detail/4bf92f3577b34da6a3ce929d0e0e4736',
        ),
        findsOneWidget,
      );

      await tester.pump(kitToastLifetime);
      await fixture.dispose();
    },
  );
}

class _SingleSurfaceCrossNavServer extends ApplicationServer {
  bool blockDepartureWithRule = false;
  bool navigateFromUnsupported = false;
  String aspireTrace = 'none';
  final actCalls = <Map<String, dynamic>>[];

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    final path = request.url.path;
    final reqBody = body(request);

    if (path.endsWith('/apps/connect')) {
      requests.add(RecordedAppRequest(path, reqBody));
      return json({
        'root': {'kind': 'view', 'name': '$brainId/apps/engineer/view'},
      });
    }

    if (path.endsWith('/apps/event')) {
      requests.add(RecordedAppRequest(path, reqBody));
      if (reqBody['key'] == 'engineer:open-aspire') {
        final argsJson = jsonEncode({
          'traceId': '4bf92f3577b34da6a3ce929d0e0e4736',
        });
        return json({
          'intent': {
            'kind': 'OpenWindow',
            'arg':
                'os://window/open?app=aspire'
                '&args=${Uri.encodeQueryComponent(argsJson)}'
                '&transition=shared-axis-x',
          },
        });
      }
      return json({'delivered': true});
    }

    if (path == '/brains/$brainId/act') {
      requests.add(RecordedAppRequest(path, reqBody));
      actCalls.add(reqBody);
      if (reqBody['signal'] == 'ui.navigate-from') {
        if (blockDepartureWithRule) {
          return json({
            'error': '. of $brainId/apps/engineer refused the command.',
            'reason': 'Rule:verification_in_flight: Live verification is in progress; wait or force departure.',
          }, status: 409);
        }
        if (navigateFromUnsupported) {
          return json({
            'error': "Screen has no [On] reaction for 'ui.navigate-from'.",
            'reason': 'HandlerFailed',
          }, status: 422);
        }
        return json({'delivered': true});
      }
      if (reqBody['signal'] == 'ui.navigate-to') {
        final args = reqBody['args'] as Map<String, dynamic>? ?? {};
        if (args['argsJson'] is String) {
          final parsed = jsonDecode(args['argsJson'] as String) as Map;
          aspireTrace = '${parsed['traceId']}';
        } else if (args['input'] is String) {
          aspireTrace = args['input'] as String;
        }
        return json({'delivered': true});
      }
      return json({'delivered': true});
    }

    if (path.endsWith('/apps/node') &&
        request.url.queryParameters['kind'] == 'view') {
      final viewName = request.url.queryParameters['name'] ?? '';
      if (viewName == '$brainId/apps/engineer/view') {
        return json({
          'generation': 1,
          'view': {
            'title': 'IntoChat Engineer',
            'acts': 'act',
            'root': {
              'kind': 'column',
              'children': [
                {'kind': 'text', 'value': 'Engineer Workbench Active'},
                {
                  'kind': 'button',
                  'key': 'engineer:open-aspire',
                  'label': 'Open Aspire Trace',
                  'intent': {'intentKind': 'OpenWindow', 'arg': ''},
                  'enabled': true,
                },
              ],
            },
          },
        });
      }
      if (viewName == '$brainId/apps/aspire/view') {
        return json({
          'generation': 2,
          'view': {
            'title': '.NET Aspire',
            'acts': 'act',
            'root': {
              'kind': 'text',
              'value':
                  'Aspire DeepLink: http://localhost:18888/traces/detail/$aspireTrace',
            },
          },
        });
      }
    }

    return super.send(request);
  }
}
