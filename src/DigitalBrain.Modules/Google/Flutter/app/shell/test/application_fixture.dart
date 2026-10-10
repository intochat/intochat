import 'dart:async';
import 'dart:convert';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_flutter_shell/workspace/home/home_screen.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
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
    'surface' => {
      'definition': {
        'mode': 'column',
        'children': [
          {'kind': 'layout', 'name': '$root/messages'},
          {'kind': 'layout', 'name': '$root/prompts'},
          {'kind': 'textfield', 'name': '$root/draft'},
          {'kind': 'layout', 'name': '$root/footer'},
        ],
        'extents': [-1, 50, 70, 48],
      },
    },
    'layout' =>
      name.endsWith('/messages')
          ? {'mode': 'list', 'children': <Object>[]}
          : name.endsWith('/prompts')
          ? {
              'mode': 'row',
              'children': [
                {'kind': 'button', 'name': '$root/prompt-one'},
                {'kind': 'button', 'name': '$root/prompt-two'},
              ],
            }
          : {
              'mode': 'row',
              'children': [
                {'kind': 'fileinput', 'name': '$root/file'},
                {'kind': 'voiceinput', 'name': '$root/voice'},
                {'kind': 'button', 'name': '$root/send'},
              ],
            },
    'textfield' => {'label': 'Message', 'kind': 'multiline', 'value': ''},
    'voiceinput' => {'label': 'Dictate'},
    'fileinput' => {'label': 'Attach'},
    'button' => {
      'label': name.endsWith('prompt-one')
          ? 'Ask the assistant'
          : name.endsWith('prompt-two')
          ? 'Work with Salesforce'
          : 'Send',
      'enabled': true,
      'action': 'click',
    },
    _ => null,
  };
}

/// One request the fake kernel received, in arrival order.
class RecordedAppRequest {
  RecordedAppRequest(this.path, this.body);

  final String path;
  final Map<String, dynamic> body;
}

/// The fake kernel behind the shell: it records app requests and answers the
/// workspace, the installed apps and every node a rendered surface asks for.
class ApplicationServer extends http.BaseClient {
  ApplicationServer({this.brainId = 'brain-1'});

  final String brainId;
  final List<RecordedAppRequest> requests = [];
  final events = StreamController<List<int>>.broadcast();

  /// The assistant committed its window: an "open" request was posted, so the kernel's
  /// workspace view reports it open, exactly as the real app answers through its neurons.
  bool assistantOpened = false;

  /// The surface neuron the shell last rendered.
  String? renderedSurface;

  List<RecordedAppRequest> requestsTo(String path) => [
    for (final request in requests)
      if (request.path == path) request,
  ];

  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    // The Home graph's brain feed is not served here: it shows its error instead of spinning forever.
    if (request.url.path.endsWith('/brain/events')) {
      return http.StreamedResponse(const Stream.empty(), 404);
    }
    if (request.url.path.endsWith('/events')) {
      return http.StreamedResponse(events.stream, 200);
    }
    requests.add(RecordedAppRequest(request.url.path, _body(request)));
    if (request.url.path.endsWith('/apps/assistant/request') &&
        requests.last.body['operation'] == 'open') {
      assistantOpened = true;
    }
    if (request.url.queryParameters['kind'] == 'surface') {
      renderedSurface = request.url.queryParameters['name'];
    }
    if (request.url.path.endsWith('/apps/assistant')) {
      return _json(assistantView);
    }
    if (request.url.path.endsWith('/connections')) {
      return _json(const <Object>[]);
    }
    if (request.url.path.endsWith('/apps')) {
      return _json(const [assistantSummary]);
    }
    final application = applicationResponse(request);
    if (application != null) return _json(application);
    final brain = request.url.pathSegments.length > 1
        ? request.url.pathSegments[1]
        : brainId;
    return _json({
      'revision': assistantOpened ? 1 : 0,
      'windows': assistantOpened
          ? [
              {
                'id': '$brain/apps/assistant/window',
                'title': 'Assistant',
                'reference': {
                  'kind': 'surface',
                  'neuronId': '$brain/apps/assistant/surface',
                },
                'isOpen': true,
              },
            ]
          : <Object>[],
      'apps': <Object>[],
    });
  }

  Map<String, dynamic> _body(http.BaseRequest request) {
    if (request is! http.Request || request.body.isEmpty) {
      return <String, dynamic>{};
    }
    return Map<String, dynamic>.from(jsonDecode(request.body) as Map);
  }

  http.StreamedResponse _json(Object value) => http.StreamedResponse(
    Stream.value(utf8.encode(jsonEncode(value))),
    200,
    headers: {'content-type': 'application/json'},
  );
}

/// The shell started against the fake kernel, with one workspace; Home is the start view,
/// the dock is the assistant island.
class ApplicationFixture {
  ApplicationFixture._(this.tester, this.server, this.store, this.client);

  final WidgetTester tester;
  final ApplicationServer server;
  final WorkspaceStore store;
  final DigitalBrainUiClient client;

  /// The workspace id the shell actually uses (the store''s own project).
  String get brainId => store.currentProject.id;

  /// The surface neuron the shell last rendered.
  String? get renderedSurface => server.renderedSurface;

  static Future<ApplicationFixture> start(WidgetTester tester) async {
    final server = ApplicationServer();
    final store = WorkspaceStore(persistence: MemoryWorkspacePersistence());
    final client = DigitalBrainUiClient(
      baseUri: Uri.parse('http://test'),
      httpClient: server,
    )..defaultBrainId = server.brainId;
    await tester.pumpWidget(
      WorkspaceApp(store: store, programmingClient: client),
    );
    await tester.pumpAndSettle();
    return ApplicationFixture._(tester, server, store, client);
  }

  Future<void> Function(String)? _open;

  /// Opens an app the way the Home screen does and settles what it renders.
  /// The Home island hides once a window opens, so its callback is taken once.
  Future<void> openApp(String name) async {
    _open ??= tester.widget<HomeScreen>(find.byType(HomeScreen)).onOpen;
    await _open!(name);
    await tester.pumpAndSettle();
  }

  Future<void> dispose() async {
    await tester.pumpWidget(const SizedBox());
    await server.events.close();
    client.close();
    store.dispose();
  }
}
