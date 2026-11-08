part of 'ui_client.dart';

extension IntoChatOSUiClientSurfaces on IntoChatOSUiClient {
  Future<List<TableSummary>> listTables(String workspace) async {
    final body = await _tableRequest(
      'GET',
      '/brains/${Uri.encodeComponent(workspace)}/ui/tables',
    );
    return (body as List)
        .map(
          (item) =>
              TableSummary.fromJson(Map<String, dynamic>.from(item as Map)),
        )
        .toList();
  }

  /// This brain's apps, with where each installed copy came from and whether an update is offered.
  Future<List<InstalledAppSummary>> listInstalledApps(String workspace) async {
    final body = await _tableRequest(
      'GET',
      '/brains/${Uri.encodeComponent(workspace)}/apps',
    );
    return (body as List)
        .map(
          (item) => InstalledAppSummary.fromJson(
            Map<String, dynamic>.from(item as Map),
          ),
        )
        .toList();
  }

  /// The manifest an app declares, read from the same view its detail page uses.
  Future<AppManifest> readAppManifest(String brainId, String appId) async {
    final body = await jsonRequest(
      'GET',
      '/brains/${Uri.encodeComponent(brainId)}/apps/${Uri.encodeComponent(appId)}',
    );
    return AppManifest.fromJson(
      Map<String, dynamic>.from((body as Map)['manifest'] as Map),
    );
  }

  /// Publishes a request to an app; the app answers through neurons, never through this call.
  Future<String> requestApp(
    String brainId,
    String appId,
    String operation,
    String input,
  ) async {
    final body = await jsonRequest('POST', IntoChatOSUiClient.appRequestPath(brainId, appId), {
      'operation': operation,
      'input': input,
    });
    return (body as Map)['requestId'] as String;
  }

  /// Raw bytes of a served file such as an app icon; null when the server has none.
  Future<Uint8List?> readBytes(String path) async {
    try {
      final response = await _request(
        'GET',
        path,
        timeout: const Duration(seconds: 30),
      );
      return response.bodyBytes;
    } on UiRequestException catch (error) {
      if (error.statusCode == 404) return null;
      rethrow;
    }
  }

  Future<TableSnapshot> readTable(
    String workspace,
    String id, {
    int offset = 0,
    int limit = 50,
  }) async => TableSnapshot.fromJson(
    Map<String, dynamic>.from(
      await _tableRequest(
        'GET',
        '/brains/${Uri.encodeComponent(workspace)}/ui/tables/${Uri.encodeComponent(id)}?offset=$offset&limit=$limit',
      ) as Map,
    ),
  );

  Future<TableSnapshot> updateTableView(
    String workspace,
    String id,
    TableViewUpdate update,
  ) async => TableSnapshot.fromJson(
    Map<String, dynamic>.from(
      await _tableRequest(
        'POST',
        '/brains/${Uri.encodeComponent(workspace)}/ui/tables/${Uri.encodeComponent(id)}/view',
        body: update.toJson(),
      ) as Map,
    ),
  );

  Future<WorkspaceSnapshot> readWorkspace(
    String workspaceId, {
    Future<void>? cancelled,
  }) async => WorkspaceSnapshot.fromJson(
    Map<String, dynamic>.from(
      await _tableRequest(
        'GET',
        '/brains/${Uri.encodeComponent(workspaceId)}',
        cancelled: cancelled,
      ) as Map,
    ),
  );

  Future<WorkspaceSnapshot> showNeuron(
    String workspace,
    String key,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'show', operationId, expectedRevision, key);
  Future<WorkspaceSnapshot> backNeuron(
    String workspace,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'back', operationId, expectedRevision);
  Future<WorkspaceSnapshot> homeNeuron(
    String workspace,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'home', operationId, expectedRevision);
  Future<WorkspaceSnapshot> closeNeuron(
    String workspace,
    String key,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'close', operationId, expectedRevision, key);

  Future<WorkspaceSnapshot> _navigateNeuron(
    String workspace,
    String command,
    String operationId,
    int expectedRevision, [
    String? key,
  ]) async => WorkspaceSnapshot.fromJson(
    Map<String, dynamic>.from(
      await _tableRequest(
        'POST',
        '/brains/${Uri.encodeComponent(workspace)}/$command',
        body: {
          'app': ?key,
          'operationId': operationId,
          'expectedRevision': expectedRevision,
        },
      ) as Map,
    ),
  );

  Future<void> reportProblem({
    required String workspaceId,
    required String intentId,
    required String message,
  }) async {
    await _tableRequest(
      'POST',
      '/brains/${Uri.encodeComponent(workspaceId)}/reports',
      body: {'intentId': intentId, 'message': message},
    );
  }

  /// The brain's neurons as snapshots: the whole brain with [brainId], or one chat's view with [chatName].
  Stream<BrainSnapshot> watchBrain({String? chatName, String? brainId}) {
    if ((chatName == null) == (brainId == null)) {
      throw ArgumentError('Name a chat or a brain, not both.');
    }
    final path = brainId != null
        ? '/brains/${Uri.encodeComponent(brainId)}/brain/events'
        : '/chats/${Uri.encodeComponent(chatName!)}/brain/events';
    final abort = Completer<void>();
    late StreamController<BrainSnapshot> controller;
    controller = StreamController<BrainSnapshot>(
      onListen: () async {
        try {
          final request = http.AbortableRequest(
            'GET',
            baseUri.resolve(path),
            abortTrigger: abort.future,
          )..headers['accept'] = 'text/event-stream';
          final response = await _http.send(request);
          if (response.statusCode != 200) {
            throw StateError(
              'Brain stream unavailable (${response.statusCode}).',
            );
          }
          String? event;
          final data = <String>[];
          await for (final line
              in response.stream
                  .transform(utf8.decoder)
                  .transform(const LineSplitter())) {
            if (abort.isCompleted) break;
            if (line.startsWith('event:')) event = line.substring(6).trim();
            if (line.startsWith('data:')) {
              data.add(line.substring(5).trimLeft());
            }
            if (line.isEmpty) {
              if (event == 'brain-snapshot' && data.isNotEmpty) {
                controller.add(
                  BrainSnapshot.fromJson(
                    jsonDecode(data.join('\n')) as Map<String, dynamic>,
                  ),
                );
              }
              event = null;
              data.clear();
            }
          }
        } catch (error, stack) {
          if (!abort.isCompleted) controller.addError(error, stack);
        } finally {
          if (!controller.isClosed) await controller.close();
        }
      },
      onCancel: () {
        if (!abort.isCompleted) abort.complete();
      },
    );
    return controller.stream;
  }

  /// Spend and balance for one brain, grouped by [by]; rows are most expensive first.
  Future<ComputeSummary> readCompute(
    String brainId, {
    String by = 'app',
    DateTime? since,
  }) async {
    final query = Uri(
      queryParameters: {'by': by, 'since': ?since?.toUtc().toIso8601String()},
    ).query;
    return ComputeSummary.fromJson(
      Map<String, dynamic>.from(
        await _tableRequest(
          'GET',
          '/brains/${Uri.encodeComponent(brainId)}/compute?$query',
        ) as Map,
      ),
    );
  }

  /// The most recent individual costs for one brain, newest [take] first.
  Future<List<ComputeCost>> readComputeRecent(
    String brainId, {
    int take = 50,
  }) async {
    final body = await _tableRequest(
      'GET',
      '/brains/${Uri.encodeComponent(brainId)}/compute/recent?take=$take',
    );
    return [
      for (final item in body as List)
        ComputeCost.fromJson(Map<String, dynamic>.from(item as Map)),
    ];
  }

  Future<ComputeTracePage> readComputeTrace(
    String brainId,
    String traceId, {
    int take = 100,
    String? continuationToken,
  }) async {
    if (take < 1 || take > 500) {
      throw ArgumentError.value(take, 'take', 'Must be between 1 and 500.');
    }
    final query = Uri(
      queryParameters: {
        'take': '$take',
        'continuationToken': ?continuationToken,
      },
    ).query;
    return ComputeTracePage.fromJson(
      Map<String, dynamic>.from(
        await _tableRequest(
          'GET',
          '/brains/${Uri.encodeComponent(brainId)}/compute/traces/${Uri.encodeComponent(traceId)}?$query',
        ) as Map,
      ),
    );
  }

  Future<void> postUi(String path, {Map<String, Object?>? body}) async {
    await _request(
      'POST',
      path,
      body: body,
      timeout: const Duration(seconds: 10),
    );
  }

  Future<BrainSnapshot> readBrain({required String chatName}) async {
    final response = await _request(
      'GET',
      '/chats/${Uri.encodeComponent(chatName)}/brain',
      timeout: const Duration(seconds: 10),
    );
    return BrainSnapshot.fromJson(
      jsonDecode(response.body) as Map<String, dynamic>,
    );
  }

  Future<Map<String, dynamic>> appRequest(
    String workspace,
    String path, {
    Map<String, Object?>? body,
  }) async {
    final response = await _request(
      body == null ? 'GET' : 'POST',
      '/brains/${Uri.encodeComponent(workspace)}/apps/$path',
      body: body,
      timeout: const Duration(seconds: 30),
    );
    return Map<String, dynamic>.from(jsonDecode(response.body) as Map);
  }

  /// Calls the developer-mode C# file API; [path] is relative to `/csharp` (empty for the list).
  Future<Map<String, dynamic>> csharpRequest(
    String workspace,
    String method,
    String path, {
    Map<String, Object?>? body,
  }) async {
    try {
      final result = await _tableRequest(
        method,
        '/brains/${Uri.encodeComponent(workspace)}/csharp${path.isEmpty ? '' : '/$path'}',
        body: body,
        // Starting builds the app inside a .NET SDK container, which can take minutes.
        timeout: const Duration(minutes: 3),
      );
      return result is Map ? Map<String, dynamic>.from(result) : {};
    } on TableRequestException catch (error) {
      throw CSharpFileRequestException(error.statusCode, error.message);
    }
  }

  Future<Map<String, dynamic>> storeSecret(
    String name,
    String label,
    String value,
  ) async {
    final owner = principalId;
    if (owner == null) {
      throw StateError('Sign in before storing a secret.');
    }
    final response = await _request(
      'POST',
      '/secrets/${Uri.encodeComponent(owner)}',
      body: {'name': name, 'label': label, 'value': value},
      timeout: const Duration(seconds: 30),
    );
    return response.body.isEmpty
        ? <String, dynamic>{}
        : Map<String, dynamic>.from(jsonDecode(response.body) as Map);
  }

  Future<Object?> connectorRequest(
    String workspaceId,
    String path, {
    Map<String, Object?>? body,
  }) {
    if (path.isNotEmpty) {
      throw ArgumentError.value(
        path,
        'path',
        'The connector route is the catalog only.',
      );
    }
    return jsonRequest('GET', '/connectors');
  }

  Future<Object?> workspaceConnectionsRequest(
    String workspaceId,
    String path, {
    Map<String, Object?>? body,
  }) => jsonRequest(
    body == null ? 'GET' : 'POST',
    '/brains/${Uri.encodeComponent(workspaceId)}/connections${path.isEmpty ? '' : '/$path'}',
    body,
  );

  Future<List<Map<String, dynamic>>> listWorkspaceConnections(
    String workspaceId,
  ) async {
    final result = await workspaceConnectionsRequest(workspaceId, '');
    return [
      for (final item in (result as List? ?? const []))
        Map<String, dynamic>.from(item as Map),
    ];
  }

  Future<Map<String, dynamic>> connectWorkspaceSecret(
    String workspaceId,
    String connectionId, {
    required String connectorId,
    required String authMethodId,
    required String value,
  }) async {
    final result = await workspaceConnectionsRequest(
      workspaceId,
      '${Uri.encodeComponent(connectionId)}/connect',
      body: {
        'connectorId': connectorId,
        'authMethodId': authMethodId,
        'value': value,
      },
    );
    return result is Map ? Map<String, dynamic>.from(result) : {};
  }

  Future<Map<String, dynamic>> startWorkspaceConnectionLogin(
    String workspaceId,
    String connectionId, {
    required String connectorId,
    required String authMethodId,
    String? target,
  }) async {
    final result = await workspaceConnectionsRequest(
      workspaceId,
      '${Uri.encodeComponent(connectionId)}/login',
      body: {
        'connectorId': connectorId,
        'authMethodId': authMethodId,
        'target': ?target,
      },
    );
    return result is Map ? Map<String, dynamic>.from(result) : {};
  }

  Future<Map<String, dynamic>> probeWorkspaceConnection(
    String workspaceId,
    String connectionId,
  ) async {
    final result = await workspaceConnectionsRequest(
      workspaceId,
      '${Uri.encodeComponent(connectionId)}/probe',
      body: const {},
    );
    return result is Map ? Map<String, dynamic>.from(result) : {};
  }

  Future<void> disconnectWorkspaceConnection(
    String workspaceId,
    String connectionId,
  ) async {
    await workspaceConnectionsRequest(
      workspaceId,
      '${Uri.encodeComponent(connectionId)}/disconnect',
      body: const {},
    );
  }

  Future<Object?> workspaceWindowsRequest(
    String workspaceId,
    String path, {
    Map<String, Object?>? body,
  }) => jsonRequest(
    body == null ? 'GET' : 'POST',
    '/brains/${Uri.encodeComponent(workspaceId)}/windows/$path',
    body,
  );

}
