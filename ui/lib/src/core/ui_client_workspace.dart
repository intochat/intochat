part of 'ui_client.dart';

extension IntoChatOSUiClientWorkspace on IntoChatOSUiClient {
  /// The brain's shared watch: every node watch of a screen rides one `Watch` stream.
  NeuronWatchMux watches(String brainId) =>
      _watches[brainId] ??= NeuronWatchMux(() => channel, brainId);

  /// The node's own change signal; the view reads the node once per event.
  Stream<NeuronSignal> watchNode(String brainId, String kind, String name) {
    if (kind == 'screen' ||
        (kind == 'view' && _isExplicitScreenAddress(name))) {
      final source =
          _screenNeuronKey(brainId, name) ?? _unitScreenKey(brainId, name);
      return watches(brainId)
          .watch(
            WatchSourceSpec(IntoChatOSUiClient.screenContract, source, const [
              IntoChatOSUiClient.screenChangedSignal,
            ]),
          )
          .map(_logSignalLatency);
    }
    final contract = NodeContracts.of(kind);
    final source = kind.startsWith('ui.') && !name.startsWith('$brainId/')
        ? '$brainId/$name'
        : name;
    final signals = kind == 'confirmation'
        ? ['ConfirmationRequested', 'ConfirmationResolved']
        : [contract.changed];
    return watches(brainId)
        .watch(WatchSourceSpec(contract.contract, source, signals))
        .map(_logSignalLatency);
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

  /// Publishes a request to an app; the app answers through neurons, never through this call.
  Future<String> requestApp(
    String brainId,
    String appId,
    String operation,
    String input,
  ) async {
    final body = await jsonRequest(
      'POST',
      IntoChatOSUiClient.appRequestPath(brainId, appId),
      {'operation': operation, 'input': input},
    );
    return (body as Map)['requestId'] as String;
  }

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

  Future<WorkspaceSnapshot> homeNeuron(
    String workspace,
    String operationId,
    int expectedRevision,
  ) => _navigateNeuron(workspace, 'home', operationId, expectedRevision);

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

  /// The brain's journal as batches of signals after [since] (a sequence number), each new one as it is journaled; the
  /// stream reopens from the last sequence seen when the connection drops and fails with [JournalTailRefused] when refused.
  Stream<List<JournalTailSignal>> watchJournal(
    String brainId, {
    int since = 0,
  }) => JournalTailClient(
    baseUri: baseUri,
    httpClient: _http,
  ).watch(brainId, since: since);

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

  /// The brain's scenes over this client's authenticated session.
  SceneClient scenes(String workspace) =>
      SceneClient(_http, baseUri, workspace);

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

  /// Reads the typed desktop shell projection (`GET /brains/{workspace}/apps/desktop/projection`).
  Future<DesktopShellProjection> readDesktopProjection(
    String workspace,
  ) async => DesktopShellProjection.fromJson(
    await appRequest(workspace, 'desktop/projection'),
  );

  /// Reads one UI node of the brain (`GET /brains/{workspace}/apps/node?kind=...&name=...`,
  /// or for `kind == 'child'` its key path `GET /brains/{workspace}/apps/{app}/children/{path}`)
  /// and decodes it into a typed [UiNodePayload].
  Future<UiNodePayload> readNode(
    String workspace,
    String kind,
    String name,
  ) async {
    final path = _nodePath(kind, name);
    final response = await _request(
      'GET',
      '/brains/${Uri.encodeComponent(workspace)}/apps/$path',
      timeout: const Duration(seconds: 30),
    );
    final Object? decoded = response.body.isEmpty
        ? null
        : jsonDecode(response.body);
    return UiNodePayload.fromDynamic(kind, decoded);
  }

  /// Posts one typed [UiAction] from a [NodeContext] or [ViewHost]:
  /// - [ActAction] -> `POST /brains/{workspace}/act` (`AppUiEndpoints.ActRequest`)
  /// - [ConfirmationAction] -> [answerConfirmation]
  /// - [WebBrowserAction] -> [webBrowser]
  Future<Map<String, dynamic>> uiEvent(
    String workspace,
    UiAction action,
  ) async => switch (action) {
    ActAction() => _act(workspace, action),
    ConfirmationAction(:final planId, :final approve) => answerConfirmation(
      workspace,
      planId,
      approve: approve,
    ),
    WebBrowserAction() => webBrowser(workspace, action),
  };

  Future<Map<String, dynamic>> _act(String workspace, ActAction action) async {
    final response = await _request(
      'POST',
      '/brains/${Uri.encodeComponent(workspace)}/act',
      body: action.toRequestJson(),
      timeout: const Duration(seconds: 30),
    );
    return Map<String, dynamic>.from(jsonDecode(response.body) as Map);
  }

  /// Answers one plan of the brain's confirmation inbox as the person:
  /// `POST /brains/{workspace}/confirmations/{planId}` `{approve}`
  /// (ConfirmationEndpoints). A plan id may hold `/`; each segment is escaped.
  Future<Map<String, dynamic>> answerConfirmation(
    String workspace,
    String planId, {
    required bool approve,
  }) async {
    final response = await _request(
      'POST',
      '/brains/${Uri.encodeComponent(workspace)}/confirmations/'
          '${planId.split('/').map(Uri.encodeComponent).join('/')}',
      body: {'approve': approve},
      timeout: const Duration(seconds: 30),
    );
    return _bodyMap(response.body);
  }

  /// Reports the embedded browser's attachment to its browser neuron:
  /// `POST /brains/{workspace}/browsers/{connect|disconnect|unavailable}`
  /// with `{name, port, sessionId}`, `{name, sessionId}` or `{name, reason}`
  /// (WebBrowserEndpoints).
  Future<Map<String, dynamic>> webBrowser(
    String workspace,
    WebBrowserAction report,
  ) async {
    final response = await _request(
      'POST',
      '/brains/${Uri.encodeComponent(workspace)}/browsers/${report.action}',
      body: report.toRequestJson(),
      timeout: const Duration(seconds: 30),
    );
    return _bodyMap(response.body);
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

bool _isExplicitScreenAddress(String name) {
  final trimmed = name.trim();
  if (trimmed.isEmpty) return false;
  final parts = trimmed.split('/');
  return parts[0].startsWith('app.') ||
      (parts.length >= 2 && parts[1].startsWith('app.'));
}

String? _screenNeuronKey(String brainId, String name, {String? alias}) {
  final trimmed = name.trim();
  if (trimmed.isEmpty) return null;
  final withoutView = trimmed.endsWith('/view')
      ? trimmed.substring(0, trimmed.length - 5)
      : trimmed;
  final parts = withoutView.split('/');
  if (parts.any((p) => p.isEmpty || p == '.' || p == '..')) return null;

  if (alias != null && alias.trim().isNotEmpty && !alias.contains('/')) {
    final cleanAlias = alias.trim();
    final scope =
        (parts.length >= 3 && parts[1] == 'apps') ||
            (parts.length >= 2 &&
                parts[0] != 'apps' &&
                !parts[0].startsWith('app.'))
        ? parts[0]
        : brainId;
    return scope.isEmpty ? '$cleanAlias/default' : '$scope/$cleanAlias/default';
  }

  if (parts.length == 3 && parts[1] == 'apps') {
    return '${parts[0]}/app.${parts[2]}/default';
  }
  if (parts.length == 2 && parts[0] == 'apps') {
    return brainId.isEmpty
        ? 'app.${parts[1]}/default'
        : '$brainId/app.${parts[1]}/default';
  }
  if (parts.length == 3 && parts[1].startsWith('app.')) {
    return withoutView;
  }
  if (parts.length == 2 && parts[0].startsWith('app.')) {
    return brainId.isEmpty ? withoutView : '$brainId/$withoutView';
  }
  if (parts.length == 2 && parts[1].startsWith('app.')) {
    return '${parts[0]}/${parts[1]}/default';
  }
  if (parts.length == 1 && parts[0].startsWith('app.')) {
    return brainId.isEmpty
        ? '${parts[0]}/default'
        : '$brainId/${parts[0]}/default';
  }
  return null;
}

String _unitScreenKey(String brainId, String name) {
  final trimmed = name.trim();
  final parts = trimmed.split('/');
  if (parts.length == 3) return trimmed;
  if (parts.length == 2) {
    return parts[0] == brainId
        ? '$brainId/${parts[1]}/default'
        : (brainId.isEmpty ? trimmed : '$brainId/$trimmed');
  }
  return brainId.isEmpty ? '$trimmed/default' : '$brainId/$trimmed/default';
}

NeuronSignal _logSignalLatency(NeuronSignal signal) {
  // Delivery latency as the comparison measures it: envelope stamp to arrival.
  developer.log(
    'neuron.latency ms=${DateTime.now().millisecondsSinceEpoch - signal.serverTimeMs} type=${signal.type} source=${signal.source}',
    name: 'intochatos.channel',
  );
  return signal;
}

Map<String, dynamic> _bodyMap(String body) => body.isEmpty
    ? <String, dynamic>{}
    : Map<String, dynamic>.from(jsonDecode(body) as Map);

String _nodePath(String kind, String name) {
  if (kind == 'child') {
    final at = name.indexOf('/apps/');
    final rest = at < 0 ? '' : name.substring(at + '/apps/'.length);
    final children = rest.indexOf('/children/');
    if (children <= 0) {
      throw ArgumentError.value(name, 'name', 'not an app child');
    }
    final path = rest
        .substring(children + '/children/'.length)
        .split('/')
        .map(Uri.encodeComponent)
        .join('/');
    return '${Uri.encodeComponent(rest.substring(0, children))}/children/$path';
  }
  return 'node?${Uri(queryParameters: {'kind': kind, 'name': name}).query}';
}
