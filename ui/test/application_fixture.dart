import 'dart:async';
import 'dart:convert';

import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_app.dart';
import 'package:intochat_os_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

const assistantSummary = {
  'name': 'assistant',
  'title': 'Assistant',
  'description': 'Chat with your brain',
  'status': 'Live',
  'version': '1.0.0',
  'operations': ['open'],
};

/// GET /brains/{b}/apps/assistant: the view the dock reads its toolbar from.
const assistantView = {
  'name': 'assistant',
  'manifest': {
    'title': 'Assistant',
    'entry': {'kind': 'view', 'name': 'surface'},
    'description': 'Chat with your brain',
    'icon': null,
    'operations': [
      {'name': 'open', 'description': ''},
      {'name': 'send', 'description': ''},
      {'name': 'new', 'description': ''},
      {'name': 'voice', 'description': ''},
    ],
    'toolbar': [
      {'operation': 'send', 'kind': 'text', 'label': 'Message your brain'},
      {'operation': 'voice', 'icon': 'mic', 'label': 'Voice'},
    ],
  },
};

/// A surface composed entirely from generic declared primitives.
///
/// Also answers an app request the way the kernel does: the request is accepted
/// at once with its id, and the app answers through neurons, never here.
Map<String, dynamic>? applicationResponse(http.BaseRequest request) {
  final segments = request.url.pathSegments;
  if (segments.length < 2 || segments.first != 'brains') return null;
  final root = '${segments[1]}/apps/assistant';
  if (request.url.path.endsWith('/request')) {
    return {'requestId': 'request-1'};
  }
  if (!request.url.path.endsWith('/apps/node')) return null;
  final name = request.url.queryParameters['name'] ?? '';
  return switch (request.url.queryParameters['kind']) {
    'viewport' => {
      'revision': 1,
      'root': {'kind': 'view', 'name': '${segments[1]}/apps/ino/surface'},
    },
    'view' =>
      name.endsWith('/apps/desktop/view')
          ? desktopPublishedView(segments[1])
          : assistantPublishedView(root),
    _ => null,
  };
}

/// The desktop view the host publishes: one Ino window above the dock.
Map<String, dynamic> desktopPublishedView(String brain) {
  Map<String, dynamic> button(
    String key,
    String label,
    String signal,
    String arg,
  ) => {
    'kind': 'button',
    'key': key,
    'label': label,
    'command': {'target': '.', 'signal': signal, 'args': arg},
    'enabled': true,
  };
  return {
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
                    'kind': 'column',
                    'children': [
                      {
                        'kind': 'row',
                        'children': [
                          button('focus-w1', 'Ino', 'focus', 'w1'),
                          button('close-w1', 'Close tab', 'close', 'w1/0'),
                        ],
                      },
                      {
                        'kind': 'neuronRef',
                        'refKind': 'slot',
                        'neuronUri':
                            '$brain/control.tabStrip/$brain/app.desktop/default/tabs-w1',
                        'props': {
                          'items': [
                            {'id': 'w1/0', 'label': 'Ino'},
                          ],
                          'selectedId': 'w1/0',
                        },
                      },
                      {
                        'kind': 'neuronRef',
                        'refKind': 'viewport',
                        'neuronUri': '$brain/apps/desktop/window/w1',
                      },
                    ],
                  },
                ],
              },
            ],
          },
          {
            'kind': 'row',
            'children': [button('dock-ino', 'Ino', 'open', 'ino')],
          },
        ],
      },
    },
  };
}

/// The assistant's published view: prompts, the draft field and Send.
Map<String, dynamic> assistantPublishedView(String root) {
  final owner = root.substring(root.indexOf('/') + 1);
  Map<String, dynamic> button(String key, String label) => {
    'kind': 'button',
    'key': key,
    'label': label,
    'command': {'target': '.', 'signal': key},
    'enabled': true,
  };
  return {
    'generation': 1,
    'view': {
      'title': 'Assistant',
      'root': {
        'kind': 'column',
        'children': [
          {'kind': 'collection', 'entries': <Object>[]},
          {
            'kind': 'row',
            'children': [
              button('prompt-one', 'Ask the assistant'),
              button('prompt-two', 'Work with Salesforce'),
            ],
          },
          {
            'kind': 'field',
            'key': 'draft',
            'name': '$owner/draft',
            'fieldKind': 'Multiline',
            'placeholder': 'Message',
          },
          {
            'kind': 'row',
            'children': [button('send', 'Send')],
          },
        ],
      },
    },
  };
}

/// One request the fake kernel received, in arrival order.
class RecordedAppRequest {
  RecordedAppRequest(this.path, this.body);

  final String path;
  final Map<String, dynamic> body;
}

class RecordingLink implements NeuronLink {
  final watches = <(String, List<WatchSourceSpec>)>[];
  final events = StreamController<NeuronSignal>.broadcast();
  @override
  Stream<NeuronSignal> watch(String brainId, List<WatchSourceSpec> sources) {
    watches.add((brainId, sources));
    return events.stream;
  }

  @override
  Future<Map<String, dynamic>?> call(
    String brainId,
    String contract,
    String key,
    String method,
    List<Object?> arguments,
  ) async => null;
  @override
  Future<dynamic> readState(
    String brainId,
    String contract,
    String key, {
    Duration? timeout,
  }) async => null;
  @override
  Future<void> shutdown() async {
    await events.close();
  }
}

class ApplicationServer extends http.BaseClient {
  ApplicationServer({this.brainId = 'brain-1'});
  final String brainId;
  final requests = <RecordedAppRequest>[];
  final events = StreamController<List<int>>.broadcast();
  final paths = <String, List<String>>{};
  int revision = 0;
  bool developerMode = false, allowed = true, failOpen = false;
  String? renderedSurface;
  Map<String, dynamic>? root;
  List<RecordedAppRequest> requestsTo(String path) =>
      requests.where((r) => r.path == path).toList();
  Map<String, dynamic> body(http.BaseRequest request) =>
      request is http.Request && request.body.isNotEmpty
      ? Map<String, dynamic>.from(jsonDecode(request.body) as Map)
      : {};
  http.StreamedResponse json(Object value, {int status = 200}) =>
      http.StreamedResponse(
        Stream.value(utf8.encode(jsonEncode(value))),
        status,
        headers: {'content-type': 'application/json'},
      );
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    requests.add(RecordedAppRequest(request.url.path, body(request)));
    final path = request.url.path;
    if (path.endsWith('/apps/connect')) return json({'root': root});
    if (path == '/session/capabilities') {
      return json({'developerMode': developerMode});
    }
    if (path == '/identity/brains') {
      return request.method == 'POST'
          ? json({'brainId': 'created-brain'})
          : json(
              allowed
                  ? [
                      {'id': 'circle-test', 'kind': 'circle'},
                    ]
                  : [],
            );
    }
    if (path == '/connectors') return json(<Object>[]);
    final brain = request.url.pathSegments.length > 1
        ? request.url.pathSegments[1]
        : brainId;
    final navigation = paths.putIfAbsent(
      brain,
      () => [brain, '$brain/apps/ino'],
    );
    if (path.endsWith('/show')) {
      if (failOpen) return json({'error': 'Unavailable'}, status: 503);
      navigation.add(body(request)['app'] as String);
      revision++;
    }
    if (path.endsWith('/home')) {
      navigation.removeRange(1, navigation.length);
      revision++;
    }
    if (path.endsWith('/back') && navigation.length > 1) {
      navigation.removeLast();
      revision++;
    }
    if (path.endsWith('/close')) {
      navigation.remove(body(request)['app']);
      revision++;
    }
    if (path.contains('/ui/scenes/')) {
      if (path.endsWith('/report')) return json({});
      final focus = navigation.last;
      final nodes = <Object>[
        for (final app in {
          'ino',
          'assistant',
          'files',
          'settings',
          if (focus.contains('/apps/')) focus.split('/apps/').last,
        })
          {
            'id': '$brain/apps/$app',
            'type': 'sphere',
            'transform': {
              'x': 0,
              'y': 0,
              'scale': focus == '$brain/apps/$app' ? 2.2 : 1,
            },
            'inputs': ['tap'],
            'parameters': {
              'icon': 'folder',
              'title': app,
              'neuron': '$brain/apps/$app',
              'focused': '${focus == '$brain/apps/$app'}',
              'state': 'ready',
              if (focus == '$brain/apps/$app')
                'main': jsonEncode({
                  'kind': 'view',
                  'name': '$brain/apps/$app/surface',
                }),
              if (focus == '$brain/apps/$app')
                'toolbar': jsonEncode(
                  app == 'ino' || app == 'assistant'
                      ? (assistantView['manifest'] as Map)['toolbar']
                      : [],
                ),
            },
          },
      ];
      return json({
        'name': '$brain/space',
        'revision': revision + 1,
        'definition': {
          'nodes': nodes,
          'camera': {'x': 0, 'y': 0, 'zoom': 1},
        },
      });
    }
    if (request.url.queryParameters['kind'] == 'view' &&
        !(request.url.queryParameters['name'] ?? '').endsWith(
          '/desktop/view',
        )) {
      renderedSurface = request.url.queryParameters['name'];
    }
    if (path == '/brains/$brain/apps') {
      return json([
        for (final id in const [
          'ino',
          'assistant',
          'files',
          'settings',
          'engineer',
          'aspire',
          'orleans',
          'desktop',
        ])
          {
            'name': id,
            'id': id,
            'title': id,
            'description': '',
            'status': 'Live',
            'version': '1.0.0',
            'operations': ['open'],
          },
      ]);
    }
    if (path.endsWith('/apps/assistant') || path.endsWith('/apps/ino')) {
      return json(assistantView);
    }
    if (path.endsWith('/connections')) return json(<Object>[]);
    if (path.endsWith('/csharp')) return json({'files': <Object>[]});
    final response = applicationResponse(request);
    if (response != null) return json(response);
    return json({
      'revision': revision,
      'path': navigation,
      'open': navigation.skip(1).toList(),
    });
  }
}

class SpaceTestView {
  const SpaceTestView(this.focused);
  final String? focused;
}

class SpaceTestHandle {
  SpaceTestHandle(this._state);
  final WorkspaceAppState _state;

  String? get brain => _state.currentBrain;
  WorkspaceSnapshot? get workspace => _state.workspaceSnapshot;
  ValueNotifier<SpaceTestView> get view =>
      ValueNotifier<SpaceTestView>(SpaceTestView(_state.focusedNeuron));
  Future<void> show(String key) => _state.show(key);
  Future<void> home() => _state.home();
}

class ApplicationFixture {
  ApplicationFixture._(
    this.tester,
    this.server,
    this.store,
    this.client,
    this.link,
  );
  final WidgetTester tester;
  final ApplicationServer server;
  final WorkspaceStore store;
  final IntoChatOSUiClient client;
  final RecordingLink link;
  String get brainId => client.defaultBrainId!;
  String? get renderedSurface => server.renderedSurface;
  WorkspaceAppState get appState =>
      tester.state<WorkspaceAppState>(find.byType(WorkspaceApp));
  late final SpaceTestHandle space = SpaceTestHandle(appState);
  static Future<ApplicationFixture> start(
    WidgetTester tester, {
    ApplicationServer? server,
    Future<void> Function()? onSwitchAccount,
  }) async {
    final api = server ?? ApplicationServer();
    final store = WorkspaceStore();
    final link = RecordingLink();
    final client =
        IntoChatOSUiClient(baseUri: Uri.parse('http://test'), httpClient: api)
          ..defaultBrainId = api.brainId
          ..link = link;
    await tester.pumpWidget(
      WorkspaceApp(
        store: store,
        programmingClient: client,
        onSwitchAccount: onSwitchAccount,
      ),
    );
    await tester.pumpAndSettle();
    return ApplicationFixture._(tester, api, store, client, link);
  }

  Future<void> openApp(String name) async {
    await space.show('$brainId/apps/$name');
    await tester.pumpAndSettle();
  }

  Future<void> menu(String label) async {
    await tester.tap(find.byTooltip('Profile'));
    await tester.pumpAndSettle();
    await tester.tap(find.text(label));
    await tester.pumpAndSettle();
  }

  Future<void> dispose() async {
    await tester.pumpWidget(const SizedBox());
    await server.events.close();
    client.close();
    store.dispose();
  }
}
