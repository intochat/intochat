import 'package:digitalbrain_flutter_shell/workspace/home/home_screen.dart';

import 'dart:async';
import 'dart:convert';

import 'application_fixture.dart';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:digitalbrain_ui/digitalbrain_ui.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_app.dart';
import 'package:digitalbrain_flutter_shell/workspace/workspace_store.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;

import 'workspace_remote_controller_test.dart' show MemoryPersistence;

Map<String, dynamic> tableJson(int revision, String company) => {
  'id': 'table',
  'title': 'Leads',
  'revision': revision,
  'columns': [
    {'id': 'company', 'label': 'company', 'type': 'text'},
  ],
  'rows': [
    {
      'id': 'row',
      'cells': [company],
    },
  ],
  'filters': [],
  'sort': null,
  'visibleColumns': ['company'],
  'totalRows': 60,
  'filteredRows': 60,
  'offset': 0,
  'limit': 25,
};

class WorkspaceClient extends http.BaseClient {
  List<Map<String, dynamic>> history = [];
  final events = StreamController<List<int>>.broadcast();
  int revision = 0;
  bool open = false;
  @override
  Future<http.StreamedResponse> send(http.BaseRequest request) async {
    // The Home graph's brain feed is not served here: it shows its error instead of spinning forever.
    if (request.url.path.endsWith('/brain/events')) {
      return http.StreamedResponse(const Stream.empty(), 404);
    }
    if (request.url.path.endsWith('/events')) {
      return http.StreamedResponse(events.stream, 200);
    }
    final application = applicationResponse(request);
    if (application != null &&
        request.url.queryParameters['kind'] == 'layout' &&
        (request.url.queryParameters['name'] ?? '').endsWith('/messages')) {
      application['children'] = [
        for (final run in history) ...[
          {'kind': 'card', 'name': "${run['runId']}/user"},
          {'kind': 'card', 'name': "${run['runId']}/assistant"},
        ],
      ];
    }
    if (request.url.queryParameters['kind'] == 'card') {
      final name = request.url.queryParameters['name']!;
      final run = history.firstWhere(
        (run) => name.startsWith('${run['runId']}/'),
      );
      return http.StreamedResponse(
        Stream.value(
          utf8.encode(
            jsonEncode({
              'title': '',
              'body': name.endsWith('/user') ? run['userText'] : run['reply'],
              'children': [],
            }),
          ),
        ),
        200,
      );
    }
    final value =
        application ??
        (request.url.path.contains('/conversations/')
            ? {'revision': 4, 'activeRun': null, 'runs': history}
            : {
                'revision': revision,
                'windows': revision == 0
                    ? []
                    : [
                        {
                          'id': 'window',
                          'title': 'Leads',
                          'reference': {'kind': 'table', 'neuronId': 'table'},
                          'isOpen': open,
                        },
                      ],
              });
    return http.StreamedResponse(
      Stream.value(utf8.encode(jsonEncode(value))),
      200,
      headers: {'content-type': 'application/json'},
    );
  }

  void publish(String workspace, {required bool isOpen}) {
    revision++;
    open = isOpen;
    events.add(
      utf8.encode(
        'data: ${jsonEncode({'brainId': workspace, 'revision': revision})}\n\n',
      ),
    );
  }
}

void main() {
  testWidgets('a single filter can be cleared through the server view', (
    tester,
  ) async {
    TableViewUpdate? submitted;
    final controller = UiTableController(
      workspace: 'w',
      snapshot: TableSnapshot.fromJson({
        ...tableJson(1, 'Filtered'),
        'filters': [
          {'columnId': 'company', 'operator': 'eq', 'value': 'Filtered'},
        ],
      }),
      update: (_, _, update) async {
        submitted = update;
        return TableSnapshot.fromJson(tableJson(2, 'All rows'));
      },
    );
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(body: UiDataTable(controller: controller)),
      ),
    );
    await tester.tap(find.text('Clear filters'));
    await tester.pumpAndSettle();
    expect(submitted!.filters, isEmpty);
    expect(submitted!.expectedRevision, 1);
    expect(find.text('All rows'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    controller.dispose();
  });
  testWidgets(
    'reload opens the requested workspace after asynchronous storage loads',
    (tester) async {
      final store = WorkspaceStore(persistence: MemoryPersistence());
      await tester.pumpWidget(
        WorkspaceApp(
          store: store,
          initialLocation: Uri.parse('/projects/${store.currentProject.id}'),
        ),
      );
      await tester.pumpAndSettle();
      expect(
        tester.widget<HomeScreen>(find.byType(HomeScreen)).workspaceId,
        store.currentProject.id,
      );
      await tester.pumpWidget(const SizedBox());
      store.dispose();
    },
  );
  test('late table read cannot overwrite newer page and conflict reloads authoritative revision', () async {
    final slow = Completer<TableSnapshot>();
    final fast = Completer<TableSnapshot>();
    var calls = 0;
    final controller = UiTableController(
      workspace: 'w',
      snapshot: TableSnapshot.fromJson(tableJson(1, 'old')),
      read: (_, _, {offset = 0, limit = 25}) => ++calls == 1
          ? slow.future
          : calls == 2
          ? fast.future
          : Future.value(TableSnapshot.fromJson(tableJson(3, 'authoritative'))),
      update: (_, _, update) async =>
          throw const TableRequestException(409, 'conflict'),
    );
    final first = controller.reload();
    final second = controller.reload();
    fast.complete(TableSnapshot.fromJson(tableJson(2, 'new')));
    await second;
    slow.complete(TableSnapshot.fromJson(tableJson(1, 'stale')));
    await first;
    expect(controller.snapshot.rows.single.cells.single, 'new');
    await controller.change(filters: []);
    expect(controller.snapshot.revision, 3);
    expect(controller.error, contains('changed elsewhere'));
    controller.dispose();
  });
  test('a refined saved view without rows re-reads the refined rows', () async {
    var reads = 0;
    final controller = UiTableController(
      workspace: 'w',
      snapshot: TableSnapshot.fromJson(tableJson(1, 'Berlin')),
      read: (_, _, {offset = 0, limit = 25}) async {
        reads++;
        return TableSnapshot.fromJson({
          ...tableJson(2, 'London'),
          'filteredRows': 6,
          'filters': [
            {'columnId': 'company', 'operator': 'eq', 'value': 'London'},
          ],
        });
      },
    );
    await controller.acceptSavedView(
      TableSnapshot.fromJson({
        ...tableJson(2, 'Leads'),
        'rows': <Object?>[],
        'filteredRows': 0,
        'filters': [
          {'columnId': 'company', 'operator': 'eq', 'value': 'London'},
        ],
      }),
    );
    expect(reads, 1);
    expect(controller.snapshot.rows.single.cells.single, 'London');
    expect(controller.snapshot.filters.single.value, 'London');
    controller.dispose();
  });
  test('an empty saved view re-reads once without looping', () async {
    var reads = 0;
    final controller = UiTableController(
      workspace: 'w',
      snapshot: TableSnapshot.fromJson(tableJson(1, 'old')),
      read: (_, _, {offset = 0, limit = 25}) async {
        reads++;
        return TableSnapshot.fromJson({
          ...tableJson(2, 'Leads'),
          'rows': <Object?>[],
          'filteredRows': 0,
        });
      },
    );
    await controller.acceptSavedView(
      TableSnapshot.fromJson({
        ...tableJson(2, 'Leads'),
        'rows': <Object?>[],
        'filteredRows': 0,
      }),
    );
    expect(reads, 1);
    expect(controller.snapshot.rows, isEmpty);
    controller.dispose();
  });
}
