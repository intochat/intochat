import '../../generated/node_models.dart' show AppLaunch;

// --- src/core/models/cluster.dart ---
final class BrainEntry {
  const BrainEntry({required this.id, required this.kind});

  final String id, kind;
  bool get isCircle => kind == 'Circle';

  factory BrainEntry.fromJson(Map<String, dynamic> json) => BrainEntry(
    id: json['id'] as String? ?? '',
    kind: json['kind'] as String? ?? 'Personal',
  );
}

// --- src/core/models/installed_app.dart ---
class InstalledAppSummary {
  const InstalledAppSummary({
    required this.id,
    required this.title,
    required this.description,
    required this.status,
    required this.version,
    required this.operations,
    this.icon,
    this.publishedVersion,
    this.edited = false,
    this.updateAvailable = false,
  });
  final String id, title, description, status, version;
  final List<String> operations;
  final String? icon, publishedVersion;
  final bool edited, updateAvailable;
  factory InstalledAppSummary.fromJson(Map<String, dynamic> json) =>
      InstalledAppSummary(
        id: json['name'] as String,
        title: json['title'] as String? ?? json['name'] as String,
        description: json['description'] as String? ?? '',
        status: json['status'] as String? ?? '',
        version: json['version'] as String? ?? '',
        operations: [
          for (final operation in json['operations'] as List? ?? const [])
            operation as String,
        ],
        icon: json['icon'] as String?,
        publishedVersion: json['publishedVersion'] as String?,
        edited: json['edited'] == true,
        updateAvailable: json['updateAvailable'] == true,
      );
}

// --- src/core/models/compute_usage.dart ---
class ComputeSummary {
  const ComputeSummary({
    required this.balanceMicros,
    required this.spentMicros,
    this.rows = const [],
    this.knownListPriceMicros,
    this.unpricedCount,
    this.pendingCount,
    this.unknownCount,
  });
  final int balanceMicros, spentMicros;
  final List<ComputeRow> rows;
  final int? knownListPriceMicros, unpricedCount, pendingCount, unknownCount;
  factory ComputeSummary.fromJson(Map<String, dynamic> json) => ComputeSummary(
    balanceMicros: (json['balanceMicros'] as num?)?.toInt() ?? 0,
    spentMicros: (json['spentMicros'] as num?)?.toInt() ?? 0,
    knownListPriceMicros: (json['knownListPriceMicros'] as num?)?.toInt(),
    unpricedCount: (json['unpricedCount'] as num?)?.toInt(),
    pendingCount: (json['pendingCount'] as num?)?.toInt(),
    unknownCount: (json['unknownCount'] as num?)?.toInt(),
    rows: [
      for (final row in json['rows'] as List? ?? const [])
        ComputeRow.fromJson(Map<String, dynamic>.from(row as Map)),
    ],
  );
}

class ComputeRow {
  const ComputeRow({
    required this.key,
    required this.micros,
    this.count = 0,
    this.knownListPriceMicros,
    this.unpricedCount,
    this.pendingCount,
    this.unknownCount,
  });
  final String key;
  final int micros, count;
  final int? knownListPriceMicros, unpricedCount, pendingCount, unknownCount;
  factory ComputeRow.fromJson(Map<String, dynamic> json) => ComputeRow(
    key: json['key'] as String,
    micros: (json['micros'] as num?)?.toInt() ?? 0,
    count: (json['count'] as num?)?.toInt() ?? 0,
    knownListPriceMicros: (json['knownListPriceMicros'] as num?)?.toInt(),
    unpricedCount: (json['unpricedCount'] as num?)?.toInt(),
    pendingCount: (json['pendingCount'] as num?)?.toInt(),
    unknownCount: (json['unknownCount'] as num?)?.toInt(),
  );
}

class ComputeCost {
  const ComputeCost({
    required this.traceId,
    required this.costId,
    required this.kind,
    required this.item,
    this.quantity = 0,
    required this.unit,
    required this.micros,
    this.synapse,
    this.app,
    this.neuron,
    this.at,
    this.listPriceMicros,
    this.estimatedListPriceMicros,
    this.pricingStatus,
    this.tariffVersion,
    this.fundingKind,
    this.outcome,
    this.invocationId,
    this.attemptState,
    this.providerRequestId,
    this.chargePolicy,
  });
  final String traceId, costId, kind, item, unit;
  final double quantity;
  final int micros;
  final String? synapse, app, neuron;
  final DateTime? at;
  final int? listPriceMicros, estimatedListPriceMicros;
  final String? pricingStatus,
      tariffVersion,
      fundingKind,
      outcome,
      invocationId,
      attemptState,
      providerRequestId,
      chargePolicy;

  bool get isPending => const {
    'reserved',
    'dispatchstarted',
    'outcomeknown',
    'uncertain',
  }.contains(attemptState);
  factory ComputeCost.fromJson(Map<String, dynamic> json) => ComputeCost(
    traceId: json['traceId'] as String,
    costId: json['costId'] as String,
    kind: json['kind'] as String,
    item: json['item'] as String,
    quantity: (json['quantity'] as num?)?.toDouble() ?? 0,
    unit: json['unit'] as String,
    micros: (json['micros'] as num?)?.toInt() ?? 0,
    synapse: json['synapse'] as String?,
    app: json['app'] as String?,
    neuron: json['neuron'] as String?,
    at: DateTime.tryParse(json['at'] as String? ?? ''),
    listPriceMicros: (json['listPriceMicros'] as num?)?.toInt(),
    estimatedListPriceMicros: (json['estimatedListPriceMicros'] as num?)
        ?.toInt(),
    pricingStatus: json['pricingStatus'] as String?,
    tariffVersion: json['tariffVersion'] as String?,
    fundingKind: json['fundingKind'] as String?,
    outcome: json['outcome'] as String?,
    invocationId: json['invocationId'] as String?,
    attemptState: json['attemptState'] as String?,
    providerRequestId: json['providerRequestId'] as String?,
    chargePolicy: json['chargePolicy'] as String?,
  );
}

class ComputeTracePage {
  const ComputeTracePage({
    this.rows = const [],
    this.chargedMicros = 0,
    this.knownListPriceMicros = 0,
    this.unpricedCount = 0,
    this.pendingCount = 0,
    this.unknownCount = 0,
    this.snapshotWatermark = 0,
    this.continuationToken,
    this.status = 'detailunavailable',
  });
  final List<ComputeCost> rows;
  final int chargedMicros,
      knownListPriceMicros,
      unpricedCount,
      pendingCount,
      unknownCount,
      snapshotWatermark;
  final String? continuationToken;
  final String status;
  factory ComputeTracePage.fromJson(Map<String, dynamic> json) =>
      ComputeTracePage(
        rows: [
          for (final row in json['rows'] as List? ?? const [])
            ComputeCost.fromJson(Map<String, dynamic>.from(row as Map)),
        ],
        chargedMicros: (json['chargedMicros'] as num?)?.toInt() ?? 0,
        knownListPriceMicros:
            (json['knownListPriceMicros'] as num?)?.toInt() ?? 0,
        unpricedCount: (json['unpricedCount'] as num?)?.toInt() ?? 0,
        pendingCount: (json['pendingCount'] as num?)?.toInt() ?? 0,
        unknownCount: (json['unknownCount'] as num?)?.toInt() ?? 0,
        snapshotWatermark: (json['snapshotWatermark'] as num?)?.toInt() ?? 0,
        continuationToken: json['continuationToken'] as String?,
        status: json['status'] as String? ?? 'detailunavailable',
      );
}

// --- src/core/models/approval_models.dart ---
enum ApprovalKind {
  flowStep('FlowStep'),
  synapseStaged('SynapseStaged'),
  storageWrite('StorageWrite'),
  grant('Grant'),
  other('Other');

  const ApprovalKind(this.wire);
  final String wire;

  static ApprovalKind fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown ApprovalKind', value),
  );
}

final class Approval {
  const Approval({
    required this.planId,
    required this.kind,
    required this.subject,
    required this.title,
    required this.detail,
    required this.requested,
    required this.expires,
    required this.traceId,
  });
  final String planId;
  final ApprovalKind kind;
  final String subject;
  final String title;
  final String detail;
  final String requested;
  final String expires;
  final String traceId;

  factory Approval.fromJson(Map<String, dynamic> json) => Approval(
    planId: json['planId'] as String,
    kind: ApprovalKind.fromWire(json['kind'] as String),
    subject: json['subject'] as String,
    title: json['title'] as String,
    detail: json['detail'] as String,
    requested: json['requested'] as String,
    expires: json['expires'] as String,
    traceId: json['traceId'] as String,
  );

  Map<String, dynamic> toJson() => {
    'planId': planId,
    'kind': kind.wire,
    'subject': subject,
    'title': title,
    'detail': detail,
    'requested': requested,
    'expires': expires,
    'traceId': traceId,
  };
}

// --- src/core/models/journal_models.dart ---
final class JournaledSignalView {
  const JournaledSignalView({
    required this.sequence,
    required this.type,
    required this.publisher,
    required this.payload,
    required this.at,
    this.traceId,
    this.causationId,
    this.alias,
  });
  final int sequence;
  final String type;
  final String publisher;
  final Map<String, dynamic> payload;
  final String at;
  final String? traceId;
  final String? causationId;

  /// The journal alias of the signal ([type] is the payload's record name); null when the source does not say.
  final String? alias;

  factory JournaledSignalView.fromJson(Map<String, dynamic> json) =>
      JournaledSignalView(
        sequence: (json['sequence'] as num).toInt(),
        type: json['type'] as String,
        publisher: json['publisher'] as String,
        payload: (json['payload'] as Map).cast<String, dynamic>(),
        at: json['at'] as String,
        traceId: json['traceId'] as String?,
        causationId: json['causationId'] as String?,
        alias: json['alias'] as String?,
      );

  Map<String, dynamic> toJson() => {
    'sequence': sequence,
    'type': type,
    'publisher': publisher,
    'payload': payload,
    'at': at,
    'traceId': traceId,
    'causationId': causationId,
    if (alias != null) 'alias': alias,
  };
}

/// One signal of the journal tail (`GET /brains/{id}/journal/tail`): who published it under which alias, never the payload.
final class JournalTailSignal {
  const JournalTailSignal({
    required this.seq,
    required this.publisher,
    required this.alias,
    required this.at,
    this.traceId,
    this.causationId,
  });
  final int seq;
  final String publisher, alias;
  final DateTime at;
  final String? traceId, causationId;

  factory JournalTailSignal.fromJson(Map<String, dynamic> json) =>
      JournalTailSignal(
        seq: (json['seq'] as num).toInt(),
        publisher: json['publisher'] as String,
        alias: json['alias'] as String,
        at: DateTime.parse(json['at'] as String),
        traceId: json['traceId'] as String?,
        causationId: json['causationId'] as String?,
      );

  /// The signals of one `journaled-signal` event's data, `{"signals":[...]}`.
  static List<JournalTailSignal> batch(Map<String, dynamic> json) =>
      (json['signals'] as List? ?? [])
          .map((item) => JournalTailSignal.fromJson((item as Map).cast()))
          .toList(growable: false);
}

// --- src/core/models/notification_models.dart ---
enum OsNotificationPriority {
  low,
  normal,
  high,
  urgent;

  static OsNotificationPriority parse(Object? raw) {
    if (raw is int) {
      return switch (raw) {
        0 => OsNotificationPriority.low,
        2 => OsNotificationPriority.high,
        3 => OsNotificationPriority.urgent,
        _ => OsNotificationPriority.normal,
      };
    }
    if (raw is String) {
      return switch (raw.toLowerCase()) {
        'low' => OsNotificationPriority.low,
        'high' => OsNotificationPriority.high,
        'urgent' => OsNotificationPriority.urgent,
        _ => OsNotificationPriority.normal,
      };
    }
    return OsNotificationPriority.normal;
  }
}

final class OsNotificationItem {
  const OsNotificationItem({
    required this.id,
    required this.brainId,
    required this.sourceApp,
    required this.title,
    required this.body,
    required this.priority,
    this.category,
    this.actionUrl,
    required this.read,
    required this.createdUtc,
    this.readUtc,
  });

  final String id;
  final String brainId;
  final String sourceApp;
  final String title;
  final String body;
  final OsNotificationPriority priority;
  final String? category;
  final String? actionUrl;
  final bool read;
  final DateTime createdUtc;
  final DateTime? readUtc;

  factory OsNotificationItem.fromJson(Map<String, dynamic> json) {
    return OsNotificationItem(
      id: (json['id'] as String?) ?? '',
      brainId: (json['brainId'] as String?) ?? '',
      sourceApp: (json['sourceApp'] as String?) ?? 'os',
      title: (json['title'] as String?) ?? '',
      body: (json['body'] as String?) ?? '',
      priority: OsNotificationPriority.parse(json['priority']),
      category: json['category'] as String?,
      actionUrl: json['actionUrl'] as String?,
      read: json['read'] == true,
      createdUtc:
          DateTime.tryParse((json['createdUtc'] as String?) ?? '') ??
          DateTime.now().toUtc(),
      readUtc: DateTime.tryParse((json['readUtc'] as String?) ?? ''),
    );
  }

  OsNotificationItem copyWith({bool? read, DateTime? readUtc}) {
    return OsNotificationItem(
      id: id,
      brainId: brainId,
      sourceApp: sourceApp,
      title: title,
      body: body,
      priority: priority,
      category: category,
      actionUrl: actionUrl,
      read: read ?? this.read,
      createdUtc: createdUtc,
      readUtc: readUtc ?? this.readUtc,
    );
  }
}

final class OsNotificationList {
  const OsNotificationList({required this.items, required this.unreadCount});

  final List<OsNotificationItem> items;
  final int unreadCount;

  factory OsNotificationList.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    final items = rawItems is List
        ? [
            for (final item in rawItems)
              if (item is Map)
                OsNotificationItem.fromJson(Map<String, dynamic>.from(item)),
          ]
        : const <OsNotificationItem>[];
    final unreadRaw = json['unreadCount'];
    final unreadCount = unreadRaw is int
        ? unreadRaw
        : items.where((item) => !item.read).length;
    return OsNotificationList(items: items, unreadCount: unreadCount);
  }
}

// --- src/core/models/presence_models.dart ---
enum PresenceState {
  idle('Idle'),
  listening('Listening'),
  working('Working'),
  searching('Searching'),
  waiting('Waiting'),
  attention('Attention'),
  paused('Paused'),
  disconnected('Disconnected');

  const PresenceState(this.wire);
  final String wire;

  static PresenceState fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown PresenceState', value),
  );
}

enum FocusTone {
  info('Info'),
  success('Success'),
  warning('Warning'),
  critical('Critical');

  const FocusTone(this.wire);
  final String wire;

  static FocusTone fromWire(String value) => values.firstWhere(
    (e) => e.wire == value,
    orElse: () => throw FormatException('Unknown FocusTone', value),
  );
}

final class FocusAction {
  const FocusAction({required this.kind, required this.argument});
  final String kind;
  final String argument;

  factory FocusAction.fromJson(Map<String, dynamic> json) => FocusAction(
    kind: json['kind'] as String,
    argument: json['argument'] as String,
  );

  Map<String, dynamic> toJson() => {'kind': kind, 'argument': argument};
}

final class Focus {
  const Focus({
    required this.target,
    required this.tone,
    required this.reason,
    this.action,
  });
  final String target;
  final FocusTone tone;
  final String reason;
  final FocusAction? action;

  factory Focus.fromJson(Map<String, dynamic> json) => Focus(
    target: json['target'] as String,
    tone: FocusTone.fromWire(json['tone'] as String),
    reason: json['reason'] as String,
    action: json['action'] == null
        ? null
        : FocusAction.fromJson((json['action'] as Map).cast<String, dynamic>()),
  );

  Map<String, dynamic> toJson() => {
    'target': target,
    'tone': tone.wire,
    'reason': reason,
    'action': action?.toJson(),
  };
}

final class Presence {
  const Presence({
    required this.state,
    this.flowId,
    this.step,
    this.flowType,
    this.neuronKind,
    this.traceId,
    this.focus,
    this.reason,
    required this.revision,
  });
  final PresenceState state;
  final String? flowId;
  final String? step;
  final String? flowType;
  final String? neuronKind;
  final String? traceId;
  final Focus? focus;
  final String? reason;
  final int revision;

  factory Presence.fromJson(Map<String, dynamic> json) => Presence(
    state: PresenceState.fromWire(json['state'] as String),
    flowId: json['flowId'] as String?,
    step: json['step'] as String?,
    flowType: json['flowType'] as String?,
    neuronKind: json['neuronKind'] as String?,
    traceId: json['traceId'] as String?,
    focus: json['focus'] == null
        ? null
        : Focus.fromJson((json['focus'] as Map).cast<String, dynamic>()),
    reason: json['reason'] as String?,
    revision: (json['revision'] as num).toInt(),
  );

  Map<String, dynamic> toJson() => {
    'state': state.wire,
    'flowId': flowId,
    'step': step,
    'flowType': flowType,
    'neuronKind': neuronKind,
    'traceId': traceId,
    'focus': focus?.toJson(),
    'reason': reason,
    'revision': revision,
  };
}

// --- src/core/models/scene_models.dart ---
List<Map<String, dynamic>> _objects(Object? value) =>
    (value as List? ?? const [])
        .whereType<Map>()
        .map((e) => e.cast<String, dynamic>())
        .toList(growable: false);

Map<String, String> _strings(Object? value) =>
    (value as Map? ?? const {}).map((key, value) => MapEntry('$key', '$value'));

Map<String, dynamic>? _object(Object? value) =>
    value is Map ? value.cast<String, dynamic>() : null;

double _number(Object? value, double fallback) =>
    (value as num?)?.toDouble() ?? fallback;

final class SceneTransform {
  const SceneTransform({
    this.x = 0,
    this.y = 0,
    this.z = 0,
    this.scale = 1,
    this.rotation = 0,
    this.opacity = 1,
  });
  final double x, y, z, scale, rotation, opacity;

  factory SceneTransform.fromJson(Map<String, dynamic> json) => SceneTransform(
    x: _number(json['x'], 0),
    y: _number(json['y'], 0),
    z: _number(json['z'], 0),
    scale: _number(json['scale'], 1),
    rotation: _number(json['rotation'], 0),
    opacity: _number(json['opacity'], 1),
  );
}

final class SceneTween {
  const SceneTween({
    required this.property,
    required this.from,
    required this.to,
    required this.milliseconds,
    this.curve = 'easeInOut',
    this.repeat = false,
  });
  final String property, curve;
  final double from, to;
  final int milliseconds;
  final bool repeat;

  factory SceneTween.fromJson(Map<String, dynamic> json) => SceneTween(
    property: json['property'] as String,
    from: _number(json['from'], 0),
    to: _number(json['to'], 0),
    milliseconds: (json['milliseconds'] as num?)?.toInt() ?? 0,
    curve: json['curve'] as String? ?? 'easeInOut',
    repeat: json['repeat'] == true,
  );
}

final class SceneNode {
  const SceneNode({
    required this.id,
    required this.type,
    this.transform = const SceneTransform(),
    this.style,
    this.parameters = const {},
    this.children = const [],
    this.tweens = const [],
    this.inputs = const [],
    this.tree,
  });
  final String id, type;
  final SceneTransform transform;
  final String? style;
  final Map<String, String> parameters;
  final List<SceneNode> children;
  final List<SceneTween> tweens;
  final List<String> inputs;
  final BehaviorTreeDef? tree;

  factory SceneNode.fromJson(Map<String, dynamic> json) {
    final transform = _object(json['transform']);
    final tree = _object(json['tree']);
    return SceneNode(
      id: json['id'] as String,
      type: json['type'] as String,
      transform: transform == null
          ? const SceneTransform()
          : SceneTransform.fromJson(transform),
      style: json['style'] as String?,
      parameters: _strings(json['parameters']),
      children: _objects(json['children'])
          .map(SceneNode.fromJson)
          .toList(growable: false),
      tweens: _objects(json['tweens'])
          .map(SceneTween.fromJson)
          .toList(growable: false),
      inputs: (json['inputs'] as List? ?? const [])
          .map((e) => '$e')
          .toList(growable: false),
      tree: tree == null ? null : BehaviorTreeDef.fromJson(tree),
    );
  }
}

final class SceneCamera {
  const SceneCamera({this.x = 0, this.y = 0, this.zoom = 1});
  final double x, y, zoom;

  factory SceneCamera.fromJson(Map<String, dynamic> json) => SceneCamera(
    x: _number(json['x'], 0),
    y: _number(json['y'], 0),
    zoom: _number(json['zoom'], 1),
  );
}

final class SceneDefinition {
  const SceneDefinition({
    this.nodes = const [],
    this.camera = const SceneCamera(),
    this.background = '#0B0F14',
  });
  final List<SceneNode> nodes;
  final SceneCamera camera;
  final String background;

  factory SceneDefinition.fromJson(Map<String, dynamic> json) {
    final camera = _object(json['camera']);
    return SceneDefinition(
      nodes: _objects(json['nodes'])
          .map(SceneNode.fromJson)
          .toList(growable: false),
      camera: camera == null
          ? const SceneCamera()
          : SceneCamera.fromJson(camera),
      background: json['background'] as String? ?? '#0B0F14',
    );
  }
}

final class SceneState {
  const SceneState({
    required this.name,
    required this.revision,
    required this.definition,
  });
  final String name;
  final int revision;
  final SceneDefinition definition;

  factory SceneState.fromJson(Map<String, dynamic> json) {
    final definition = _object(json['definition']);
    return SceneState(
      name: json['name'] as String? ?? '',
      revision: (json['revision'] as num?)?.toInt() ?? 0,
      definition: definition == null
          ? const SceneDefinition()
          : SceneDefinition.fromJson(definition),
    );
  }
}

// --- src/core/models/table_models.dart ---
final class TableRequestException implements Exception {
  const TableRequestException(this.statusCode, this.message);
  final int statusCode;
  final String message;
  @override
  String toString() => 'Table request failed ($statusCode): $message';
}

// --- src/core/models/tree_models.dart ---
final class TreeNode {
  const TreeNode({
    required this.kind,
    this.children = const [],
    this.key,
    this.value,
    this.seconds = 0,
    this.x = 0,
    this.y = 0,
    this.factor = 1,
    this.alpha = 1,
    this.amplitude = 0,
    this.memory = false,
  });

  final String kind;
  final List<TreeNode> children;
  final String? key, value;
  final double seconds, x, y, factor, alpha, amplitude;
  final bool memory;

  factory TreeNode.fromJson(Map<String, dynamic> json) => TreeNode(
    kind: json['kind'] as String,
    children: (json['children'] as List? ?? const [])
        .whereType<Map>()
        .map((e) => TreeNode.fromJson(e.cast<String, dynamic>()))
        .toList(growable: false),
    key: json['key'] as String?,
    value: json['value'] as String?,
    seconds: _number(json['seconds'], 0),
    x: _number(json['x'], 0),
    y: _number(json['y'], 0),
    factor: _number(json['factor'], 1),
    alpha: _number(json['alpha'], 1),
    amplitude: _number(json['amplitude'], 0),
    memory: json['memory'] == true,
  );

  Map<String, Object?> toJson() => {
    'kind': kind,
    'children': children.map((c) => c.toJson()).toList(growable: false),
    'key': key,
    'value': value,
    'seconds': seconds,
    'x': x,
    'y': y,
    'factor': factor,
    'alpha': alpha,
    'amplitude': amplitude,
    'memory': memory,
  };
}

final class BehaviorTreeDef {
  const BehaviorTreeDef(this.root);
  final TreeNode root;

  factory BehaviorTreeDef.fromJson(Map<String, dynamic> json) =>
      BehaviorTreeDef(
        TreeNode.fromJson((json['root'] as Map).cast<String, dynamic>()),
      );

  Map<String, Object?> toJson() => {'root': root.toJson()};
}

// --- src/core/models/desktop_projection.dart ---
final class DesktopTabProjection {
  const DesktopTabProjection({
    required this.id,
    required this.appId,
    required this.title,
    required this.selected,
  });

  final String id;
  final String appId;
  final String title;
  final bool selected;

  factory DesktopTabProjection.fromJson(Map<String, dynamic> json) =>
      DesktopTabProjection(
        id: (json['id'] as String?) ?? '',
        appId: (json['appId'] as String?) ?? '',
        title: (json['title'] as String?) ?? '',
        selected: json['selected'] == true,
      );

  Map<String, dynamic> toJson() => {
    'id': id,
    'appId': appId,
    'title': title,
    'selected': selected,
  };
}

/// Dart mirror of `DesktopWindowProjection` in `src/IntoChat.OS.Contracts/Shell/DesktopShellProjection.cs`.
final class DesktopWindowProjection {
  const DesktopWindowProjection({
    required this.id,
    required this.appId,
    required this.title,
    required this.viewportUri,
    required this.tabs,
    required this.activeTabId,
    this.isPinned = false,
    this.isFavourite = false,
  });

  final String id;
  final String appId;
  final String title;
  final String viewportUri;
  final List<DesktopTabProjection> tabs;
  final String activeTabId;
  final bool isPinned;
  final bool isFavourite;

  int get numericId {
    final digits = RegExp(r'(\d+)$').firstMatch(id)?.group(1);
    return digits != null ? int.parse(digits) : id.hashCode.abs() % 999 + 1;
  }

  int get activeTabIndex {
    final idx = tabs.indexWhere((t) => t.id == activeTabId || t.selected);
    return idx >= 0 ? idx : 0;
  }

  factory DesktopWindowProjection.fromJson(Map<String, dynamic> json) {
    final rawTabs = json['tabs'];
    return DesktopWindowProjection(
      id: (json['id'] as String?) ?? '',
      appId: (json['appId'] as String?) ?? '',
      title: (json['title'] as String?) ?? '',
      viewportUri: (json['viewportUri'] as String?) ?? '',
      tabs: rawTabs is List
          ? [
              for (final item in rawTabs)
                if (item is Map)
                  DesktopTabProjection.fromJson(
                    Map<String, dynamic>.from(item),
                  ),
            ]
          : const [],
      activeTabId: (json['activeTabId'] as String?) ?? '',
      isPinned: json['isPinned'] == true,
      isFavourite: json['isFavourite'] == true,
    );
  }

  Map<String, dynamic> toJson() => {
    'id': id,
    'appId': appId,
    'title': title,
    'viewportUri': viewportUri,
    'tabs': [for (final t in tabs) t.toJson()],
    'activeTabId': activeTabId,
    'isPinned': isPinned,
    'isFavourite': isFavourite,
  };
}

/// Dart mirror of `AppTile` in `src/IntoChat.OS.Contracts/Catalog/Apps/IInstalledApps.cs`.
final class DesktopAppTile {
  const DesktopAppTile({
    required this.id,
    required this.title,
    this.icon,
    this.category,
    this.dock,
    this.launch = AppLaunch.windowed,
    this.kind = 'compiled',
    this.description,
  });

  final String id;
  final String title;
  final String? icon;
  final String? category;
  final int? dock;
  final AppLaunch launch;
  final String kind;
  final String? description;

  static AppLaunch _parseLaunch(Object? raw) {
    if (raw is int) {
      return raw == 1 ? AppLaunch.maximized : AppLaunch.windowed;
    }
    if (raw is String) {
      return raw.trim().toLowerCase() == 'maximized'
          ? AppLaunch.maximized
          : AppLaunch.windowed;
    }
    return AppLaunch.windowed;
  }

  factory DesktopAppTile.fromJson(Map<String, dynamic> json) => DesktopAppTile(
    id: (json['id'] as String?) ?? '',
    title: (json['title'] as String?) ?? '',
    icon: json['icon'] as String?,
    category: json['category'] as String?,
    dock: (json['dock'] as num?)?.toInt(),
    launch: _parseLaunch(json['launch']),
    kind: (json['kind'] as String?) ?? 'compiled',
    description: json['description'] as String?,
  );

  Map<String, dynamic> toJson() => {
    'id': id,
    'title': title,
    'icon': icon,
    'category': category,
    'dock': dock,
    'launch': launch == AppLaunch.maximized ? 1 : 0,
    'kind': kind,
    'description': description,
  };
}

/// Dart mirror of `DesktopShellProjection` in `src/IntoChat.OS.Contracts/Shell/DesktopShellProjection.cs`.
final class DesktopShellProjection {
  const DesktopShellProjection({
    this.windows = const [],
    this.focusedViewId,
    this.pins = const [],
    this.tiles = const [],
    this.openWidgets = const [],
    this.stylePreset = 'warm-paper',
    this.barLayout = 'floating-dock',
    this.layoutMode = 'smart-desktop',
    this.isNarrow = false,
  });

  final List<DesktopWindowProjection> windows;
  final String? focusedViewId;
  final List<String> pins;
  final List<DesktopAppTile> tiles;
  final List<String> openWidgets;
  final String stylePreset;
  final String barLayout;
  final String layoutMode;
  final bool isNarrow;

  factory DesktopShellProjection.fromJson(Map<String, dynamic> json) {
    final rawWindows = json['windows'];
    final rawPins = json['pins'];
    final rawTiles = json['tiles'];
    final rawWidgets = json['openWidgets'];
    return DesktopShellProjection(
      windows: rawWindows is List
          ? [
              for (final item in rawWindows)
                if (item is Map)
                  DesktopWindowProjection.fromJson(
                    Map<String, dynamic>.from(item),
                  ),
            ]
          : const [],
      focusedViewId: json['focusedViewId'] as String?,
      pins: rawPins is List
          ? [
              for (final item in rawPins)
                if (item is String) item,
            ]
          : const [],
      tiles: rawTiles is List
          ? [
              for (final item in rawTiles)
                if (item is Map)
                  DesktopAppTile.fromJson(Map<String, dynamic>.from(item)),
            ]
          : const [],
      openWidgets: rawWidgets is List
          ? [
              for (final item in rawWidgets)
                if (item is String) item,
            ]
          : const [],
      stylePreset: (json['stylePreset'] as String?) ?? 'warm-paper',
      barLayout: (json['barLayout'] as String?) ?? 'floating-dock',
      layoutMode: (json['layoutMode'] as String?) ?? 'smart-desktop',
      isNarrow: json['isNarrow'] == true,
    );
  }

  Map<String, dynamic> toJson() => {
    'windows': [for (final w in windows) w.toJson()],
    'focusedViewId': focusedViewId,
    'pins': pins,
    'tiles': [for (final t in tiles) t.toJson()],
    'openWidgets': openWidgets,
    'stylePreset': stylePreset,
    'barLayout': barLayout,
    'layoutMode': layoutMode,
    'isNarrow': isNarrow,
  };
}
