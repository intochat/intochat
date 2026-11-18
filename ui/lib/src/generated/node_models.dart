// This is a generated file - do not edit.
//
// Generated from DigitalBrain.UI by DartNodeModelsFacts (IntoChat.OS.Architecture.Tests.Unit).

// ignore_for_file: prefer_const_constructors

Map<String, dynamic> _map(Object? value) =>
    value is Map ? Map<String, dynamic>.from(value) : const <String, dynamic>{};

List<Object?> _list(Object? value) => value is List ? value : const [];

String _string(Object? value, [String fallback = '']) =>
    value is String ? value : fallback;

String? _stringOrNull(Object? value) => value is String ? value : null;

double _double(Object? value, [double fallback = 0]) =>
    value is num ? value.toDouble() : fallback;

double? _doubleOrNull(Object? value) => value is num ? value.toDouble() : null;

int _int(Object? value, [int fallback = 0]) =>
    value is num ? value.toInt() : fallback;

int? _intOrNull(Object? value) => value is num ? value.toInt() : null;

bool _bool(Object? value, [bool fallback = false]) =>
    value is bool ? value : fallback;

T _enum<T extends Enum>(List<T> values, Object? value) =>
    _enumOrNull(values, value) ?? values.first;

T? _enumOrNull<T extends Enum>(List<T> values, Object? value) {
  if (value is num) {
    final index = value.toInt();
    return index >= 0 && index < values.length ? values[index] : null;
  }
  final name = '$value'.toLowerCase();
  for (final candidate in values) {
    if (candidate.name.toLowerCase() == name) {
      return candidate;
    }
  }
  return null;
}

enum BadgeTone { neutral, accent, done, working, error }

enum FieldKind { text, search, number, password, secret, suggest, multiline }

enum IntentKind { copy, openWindow, openSettings }

/// A node of a view, discriminated by `kind`.
sealed class Node {
  const Node();

  /// The wire discriminator.
  String get kind;

  static Node fromJson(Map<String, dynamic> json) => switch (json['kind']) {
    'column' => ColumnNode.fromJson(json),
    'row' => RowNode.fromJson(json),
    'grid' => GridNode.fromJson(json),
    'splitPane' => SplitPaneNode.fromJson(json),
    'surface' => SurfaceNode.fromJson(json),
    'tabs' => TabsNode.fromJson(json),
    'divider' => DividerNode.fromJson(json),
    'spacer' => SpacerNode.fromJson(json),
    'collection' => CollectionNode.fromJson(json),
    'heading' => HeadingNode.fromJson(json),
    'text' => TextNode.fromJson(json),
    'badge' => BadgeNode.fromJson(json),
    'tagPill' => TagPillNode.fromJson(json),
    'field' => FieldNode.fromJson(json),
    'intent' => IntentNode.fromJson(json),
    'switch' => SwitchNode.fromJson(json),
    'select' => SelectNode.fromJson(json),
    'progress' => ProgressNode.fromJson(json),
    'infoBar' => InfoBarNode.fromJson(json),
    'toast' => ToastNode.fromJson(json),
    'card' => CardNode.fromJson(json),
    'button' => ButtonNode.fromJson(json),
    'dataTable' => DataTableNode.fromJson(json),
    'chart' => ChartNode.fromJson(json),
    'approvalCard' => ApprovalCardNode.fromJson(json),
    'choiceCard' => ChoiceCardNode.fromJson(json),
    'draftCard' => DraftCardNode.fromJson(json),
    'recordCard' => RecordCardNode.fromJson(json),
    'secretCard' => SecretCardNode.fromJson(json),
    'jobCard' => JobCardNode.fromJson(json),
    'brainScene' => BrainSceneNode.fromJson(json),
    'neuronRef' => NeuronRefNode.fromJson(json),
    'appShell' => AppShellNode.fromJson(json),
    'navItem' => NavItemNode.fromJson(json),
    'statCard' => StatCardNode.fromJson(json),
    'webBrowser' => WebBrowserNode.fromJson(json),
    'conversation' => ConversationNode.fromJson(json),
    'errorLine' => ErrorLineNode.fromJson(json),
    'emptyState' => EmptyStateNode.fromJson(json),
    'metricTile' => MetricTileNode.fromJson(json),
    'diffCard' => DiffCardNode.fromJson(json),
    final kind => UnknownNode(kind is String ? kind : '', json),
  };
}

/// A kind this client does not know; the renderer shows it as unsupported.
final class UnknownNode extends Node {
  const UnknownNode(this.kind, this.json);
  @override
  final String kind;
  final Map<String, dynamic> json;
}

final class ActivityNode {
  const ActivityNode({
    required this.id,
    required this.title,
    required this.status,
    required this.detail,
    required this.icon,
    required this.category,
  });

  final String id;
  final String title;
  final String status;
  final String? detail;
  final String? icon;
  final String? category;

  factory ActivityNode.fromJson(Map<String, dynamic> json) => ActivityNode(
    id: _string(json['id']),
    title: _string(json['title']),
    status: _string(json['status'], 'done'),
    detail: _stringOrNull(json['detail']),
    icon: _stringOrNull(json['icon']),
    category: _stringOrNull(json['category']),
  );
}

final class AppShellNode extends Node {
  const AppShellNode({
    required this.key,
    required this.sidebar,
    required this.body,
    required this.toolbar,
    required this.inspector,
  });

  @override
  String get kind => 'appShell';

  final String key;
  final Node? sidebar;
  final Node body;
  final Node? toolbar;
  final Node? inspector;

  factory AppShellNode.fromJson(Map<String, dynamic> json) => AppShellNode(
    key: _string(json['key']),
    sidebar: json['sidebar'] is Map
        ? Node.fromJson(_map(json['sidebar']))
        : null,
    body: Node.fromJson(_map(json['body'])),
    toolbar: json['toolbar'] is Map
        ? Node.fromJson(_map(json['toolbar']))
        : null,
    inspector: json['inspector'] is Map
        ? Node.fromJson(_map(json['inspector']))
        : null,
  );
}

final class AppView {
  const AppView({required this.title, required this.root});

  final String title;
  final Node root;

  factory AppView.fromJson(Map<String, dynamic> json) => AppView(
    title: _string(json['title']),
    root: Node.fromJson(_map(json['root'])),
  );
}

final class ApprovalCardNode extends Node {
  const ApprovalCardNode({
    required this.key,
    required this.id,
    required this.title,
    required this.summary,
    required this.rows,
    required this.valueLabel,
    required this.systemName,
    required this.status,
    required this.resolve,
    required this.sourceNeuronUri,
    required this.icon,
    required this.capabilityLabel,
  });

  @override
  String get kind => 'approvalCard';

  final String key;
  final String id;
  final String title;
  final String summary;
  final List<ApprovalRowNode> rows;
  final String valueLabel;
  final String systemName;
  final String status;
  final Resolve? resolve;
  final String? sourceNeuronUri;
  final String? icon;
  final String? capabilityLabel;

  factory ApprovalCardNode.fromJson(Map<String, dynamic> json) =>
      ApprovalCardNode(
        key: _string(json['key']),
        id: _string(json['id']),
        title: _string(json['title']),
        summary: _string(json['summary']),
        rows: [
          for (final item in _list(json['rows']))
            ApprovalRowNode.fromJson(_map(item)),
        ],
        valueLabel: _string(json['valueLabel'], 'Value'),
        systemName: _string(json['systemName'], 'DigitalBrain'),
        status: _string(json['status'], 'pending'),
        resolve: json['resolve'] is Map
            ? Resolve.fromJson(_map(json['resolve']))
            : null,
        sourceNeuronUri: _stringOrNull(json['sourceNeuronUri']),
        icon: _stringOrNull(json['icon']),
        capabilityLabel: _stringOrNull(json['capabilityLabel']),
      );
}

final class ApprovalRowNode {
  const ApprovalRowNode({
    required this.label,
    required this.oldValue,
    required this.newValue,
    required this.amount,
    required this.warn,
  });

  final String label;
  final String oldValue;
  final String newValue;
  final String? amount;
  final bool warn;

  factory ApprovalRowNode.fromJson(Map<String, dynamic> json) =>
      ApprovalRowNode(
        label: _string(json['label']),
        oldValue: _string(json['oldValue']),
        newValue: _string(json['newValue']),
        amount: _stringOrNull(json['amount']),
        warn: _bool(json['warn']),
      );
}

final class BadgeNode extends Node {
  const BadgeNode({
    required this.label,
    required this.tone,
    required this.icon,
  });

  @override
  String get kind => 'badge';

  final String label;
  final String tone;
  final String? icon;

  factory BadgeNode.fromJson(Map<String, dynamic> json) => BadgeNode(
    label: _string(json['label']),
    tone: _string(json['tone'], 'neutral'),
    icon: _stringOrNull(json['icon']),
  );
}

final class BrainGraphNode {
  const BrainGraphNode({
    required this.id,
    required this.label,
    required this.uri,
    required this.kind,
    required this.x,
    required this.y,
    required this.z,
    required this.status,
    required this.subtitle,
    required this.select,
  });

  final String id;
  final String label;
  final String uri;
  final String kind;
  final double x;
  final double y;
  final double z;
  final String status;
  final String? subtitle;
  final ButtonNode? select;

  factory BrainGraphNode.fromJson(Map<String, dynamic> json) => BrainGraphNode(
    id: _string(json['id']),
    label: _string(json['label']),
    uri: _string(json['uri']),
    kind: _string(json['kind']),
    x: _double(json['x']),
    y: _double(json['y']),
    z: _double(json['z']),
    status: _string(json['status'], 'idle'),
    subtitle: _stringOrNull(json['subtitle']),
    select: json['select'] is Map
        ? ButtonNode.fromJson(_map(json['select']))
        : null,
  );
}

final class BrainGraphSynapse {
  const BrainGraphSynapse({
    required this.fromId,
    required this.toId,
    required this.signalLabel,
    required this.active,
  });

  final String fromId;
  final String toId;
  final String? signalLabel;
  final bool active;

  factory BrainGraphSynapse.fromJson(Map<String, dynamic> json) =>
      BrainGraphSynapse(
        fromId: _string(json['fromId']),
        toId: _string(json['toId']),
        signalLabel: _stringOrNull(json['signalLabel']),
        active: _bool(json['active']),
      );
}

final class BrainSceneNode extends Node {
  const BrainSceneNode({
    required this.nodes,
    required this.synapses,
    required this.selectedId,
    required this.pauseRotation,
    required this.zoomTargetId,
    required this.targetZoom,
  });

  @override
  String get kind => 'brainScene';

  final List<BrainGraphNode> nodes;
  final List<BrainGraphSynapse> synapses;
  final String? selectedId;
  final bool pauseRotation;
  final String? zoomTargetId;
  final double targetZoom;

  factory BrainSceneNode.fromJson(Map<String, dynamic> json) => BrainSceneNode(
    nodes: [
      for (final item in _list(json['nodes']))
        BrainGraphNode.fromJson(_map(item)),
    ],
    synapses: [
      for (final item in _list(json['synapses']))
        BrainGraphSynapse.fromJson(_map(item)),
    ],
    selectedId: _stringOrNull(json['selectedId']),
    pauseRotation: _bool(json['pauseRotation']),
    zoomTargetId: _stringOrNull(json['zoomTargetId']),
    targetZoom: _double(json['targetZoom']),
  );
}

final class ButtonNode extends Node {
  const ButtonNode({
    required this.key,
    required this.label,
    required this.command,
    required this.intent,
    required this.enabled,
    required this.tone,
    required this.tooltip,
    required this.visible,
    required this.automationName,
    required this.style,
    required this.icon,
    required this.small,
  });

  @override
  String get kind => 'button';

  final String key;
  final String label;
  final Command? command;
  final IntentNode? intent;
  final bool enabled;
  final String tone;
  final String? tooltip;
  final bool visible;
  final String automationName;
  final String style;
  final String? icon;
  final bool small;

  factory ButtonNode.fromJson(Map<String, dynamic> json) => ButtonNode(
    key: _string(json['key']),
    label: _string(json['label']),
    command: json['command'] is Map
        ? Command.fromJson(_map(json['command']))
        : null,
    intent: json['intent'] is Map
        ? IntentNode.fromJson(_map(json['intent']))
        : null,
    enabled: _bool(json['enabled'], true),
    tone: _string(json['tone'], 'neutral'),
    tooltip: _stringOrNull(json['tooltip']),
    visible: _bool(json['visible'], true),
    automationName: _string(json['automationName']),
    style: _string(json['style']),
    icon: _stringOrNull(json['icon']),
    small: _bool(json['small']),
  );
}

final class CardNode extends Node {
  const CardNode({
    required this.title,
    required this.body,
    required this.children,
  });

  @override
  String get kind => 'card';

  final String title;
  final String body;
  final List<Node> children;

  factory CardNode.fromJson(Map<String, dynamic> json) => CardNode(
    title: _string(json['title']),
    body: _string(json['body']),
    children: [
      for (final item in _list(json['children'])) Node.fromJson(_map(item)),
    ],
  );
}

final class ChartNode extends Node {
  const ChartNode({
    required this.chartKind,
    required this.title,
    required this.points,
    required this.comparePoints,
    required this.seriesName,
    required this.compareName,
    required this.sourceNeuronUri,
    required this.from,
    required this.prefix,
  });

  @override
  String get kind => 'chart';

  final String chartKind;
  final String title;
  final List<ChartPointNode> points;
  final List<double>? comparePoints;
  final String seriesName;
  final String compareName;
  final String? sourceNeuronUri;
  final String from;
  final String prefix;

  factory ChartNode.fromJson(Map<String, dynamic> json) => ChartNode(
    chartKind: _string(json['chartKind']),
    title: _string(json['title']),
    points: [
      for (final item in _list(json['points']))
        ChartPointNode.fromJson(_map(item)),
    ],
    comparePoints: json['comparePoints'] == null
        ? null
        : [for (final item in _list(json['comparePoints'])) _double(item)],
    seriesName: _string(json['seriesName']),
    compareName: _string(json['compareName']),
    sourceNeuronUri: _stringOrNull(json['sourceNeuronUri']),
    from: _string(json['from'], 'DigitalBrain'),
    prefix: _string(json['prefix']),
  );
}

final class ChartPointNode {
  const ChartPointNode({required this.label, required this.value});

  final String label;
  final double value;

  factory ChartPointNode.fromJson(Map<String, dynamic> json) => ChartPointNode(
    label: _string(json['label']),
    value: _double(json['value']),
  );
}

final class ChoiceCardNode extends Node {
  const ChoiceCardNode({
    required this.id,
    required this.question,
    required this.options,
    required this.pickedIndex,
  });

  @override
  String get kind => 'choiceCard';

  final String id;
  final String question;
  final List<ChoiceOptionNode> options;
  final int? pickedIndex;

  factory ChoiceCardNode.fromJson(Map<String, dynamic> json) => ChoiceCardNode(
    id: _string(json['id']),
    question: _string(json['question']),
    options: [
      for (final item in _list(json['options']))
        ChoiceOptionNode.fromJson(_map(item)),
    ],
    pickedIndex: _intOrNull(json['pickedIndex']),
  );
}

final class ChoiceOptionNode {
  const ChoiceOptionNode({
    required this.title,
    required this.detail,
    required this.pick,
    required this.icon,
  });

  final String title;
  final String? detail;
  final ButtonNode? pick;
  final String? icon;

  factory ChoiceOptionNode.fromJson(Map<String, dynamic> json) =>
      ChoiceOptionNode(
        title: _string(json['title']),
        detail: _stringOrNull(json['detail']),
        pick: json['pick'] is Map
            ? ButtonNode.fromJson(_map(json['pick']))
            : null,
        icon: _stringOrNull(json['icon']),
      );
}

final class CollectionNode extends Node {
  const CollectionNode({required this.entries});

  @override
  String get kind => 'collection';

  final List<Entry> entries;

  factory CollectionNode.fromJson(Map<String, dynamic> json) => CollectionNode(
    entries: [
      for (final item in _list(json['entries'])) Entry.fromJson(_map(item)),
    ],
  );
}

final class ColumnNode extends Node {
  const ColumnNode({
    required this.children,
    required this.gap,
    required this.padding,
    required this.extents,
  });

  @override
  String get kind => 'column';

  final List<Node> children;
  final double gap;
  final double padding;
  final List<double>? extents;

  factory ColumnNode.fromJson(Map<String, dynamic> json) => ColumnNode(
    children: [
      for (final item in _list(json['children'])) Node.fromJson(_map(item)),
    ],
    gap: _double(json['gap']),
    padding: _double(json['padding']),
    extents: json['extents'] == null
        ? null
        : [for (final item in _list(json['extents'])) _double(item)],
  );
}

final class Command {
  const Command({
    required this.target,
    required this.signal,
    required this.args,
  });

  final String target;
  final String signal;
  final Object? args;

  factory Command.fromJson(Map<String, dynamic> json) => Command(
    target: _string(json['target']),
    signal: _string(json['signal']),
    args: json['args'],
  );
}

final class ComposerNode {
  const ComposerNode({
    required this.key,
    required this.onSubmit,
    required this.placeholder,
    required this.enabled,
    required this.activeModelLabel,
    required this.activeProviderIcon,
    required this.dictation,
  });

  final String key;
  final Emit onSubmit;
  final String placeholder;
  final bool enabled;
  final String? activeModelLabel;
  final String? activeProviderIcon;
  final bool dictation;

  factory ComposerNode.fromJson(Map<String, dynamic> json) => ComposerNode(
    key: _string(json['key']),
    onSubmit: Emit.fromJson(_map(json['onSubmit'])),
    placeholder: _string(json['placeholder'], 'Message'),
    enabled: _bool(json['enabled'], true),
    activeModelLabel: _stringOrNull(json['activeModelLabel']),
    activeProviderIcon: _stringOrNull(json['activeProviderIcon']),
    dictation: _bool(json['dictation']),
  );
}

final class ConversationNode extends Node {
  const ConversationNode({
    required this.key,
    required this.messages,
    required this.composer,
    required this.waiting,
    required this.status,
    required this.error,
    required this.stop,
    required this.empty,
  });

  @override
  String get kind => 'conversation';

  final String key;
  final List<MessageNode> messages;
  final ComposerNode composer;
  final Node? waiting;
  final String status;
  final ErrorLineNode? error;
  final ButtonNode? stop;
  final EmptyStateNode? empty;

  factory ConversationNode.fromJson(Map<String, dynamic> json) =>
      ConversationNode(
        key: _string(json['key']),
        messages: [
          for (final item in _list(json['messages']))
            MessageNode.fromJson(_map(item)),
        ],
        composer: ComposerNode.fromJson(_map(json['composer'])),
        waiting: json['waiting'] is Map
            ? Node.fromJson(_map(json['waiting']))
            : null,
        status: _string(json['status'], 'ready'),
        error: json['error'] is Map
            ? ErrorLineNode.fromJson(_map(json['error']))
            : null,
        stop: json['stop'] is Map
            ? ButtonNode.fromJson(_map(json['stop']))
            : null,
        empty: json['empty'] is Map
            ? EmptyStateNode.fromJson(_map(json['empty']))
            : null,
      );
}

final class DataTableNode extends Node {
  const DataTableNode({
    required this.title,
    required this.columns,
    required this.rows,
    required this.sourceNeuronUri,
    required this.from,
  });

  @override
  String get kind => 'dataTable';

  final String title;
  final List<String> columns;
  final List<List<String>> rows;
  final String? sourceNeuronUri;
  final String from;

  factory DataTableNode.fromJson(Map<String, dynamic> json) => DataTableNode(
    title: _string(json['title']),
    columns: [for (final item in _list(json['columns'])) _string(item)],
    rows: [
      for (final item in _list(json['rows']))
        [for (final item1 in _list(item)) _string(item1)],
    ],
    sourceNeuronUri: _stringOrNull(json['sourceNeuronUri']),
    from: _string(json['from'], 'DigitalBrain'),
  );
}

final class DiffCardNode extends Node {
  const DiffCardNode({
    required this.id,
    required this.repo,
    required this.targetBranch,
    required this.headSha,
    required this.filesChanged,
    required this.additions,
    required this.deletions,
    required this.files,
  });

  @override
  String get kind => 'diffCard';

  final String id;
  final String repo;
  final String targetBranch;
  final String headSha;
  final int filesChanged;
  final int additions;
  final int deletions;
  final List<DiffFileNode> files;

  factory DiffCardNode.fromJson(Map<String, dynamic> json) => DiffCardNode(
    id: _string(json['id']),
    repo: _string(json['repo']),
    targetBranch: _string(json['targetBranch']),
    headSha: _string(json['headSha']),
    filesChanged: _int(json['filesChanged']),
    additions: _int(json['additions']),
    deletions: _int(json['deletions']),
    files: [
      for (final item in _list(json['files']))
        DiffFileNode.fromJson(_map(item)),
    ],
  );
}

final class DiffFileNode {
  const DiffFileNode({
    required this.path,
    required this.changeKind,
    required this.additions,
    required this.deletions,
    required this.hunks,
  });

  final String path;
  final String changeKind;
  final int additions;
  final int deletions;
  final List<DiffHunkNode> hunks;

  factory DiffFileNode.fromJson(Map<String, dynamic> json) => DiffFileNode(
    path: _string(json['path']),
    changeKind: _string(json['changeKind']),
    additions: _int(json['additions']),
    deletions: _int(json['deletions']),
    hunks: [
      for (final item in _list(json['hunks']))
        DiffHunkNode.fromJson(_map(item)),
    ],
  );
}

final class DiffHunkNode {
  const DiffHunkNode({required this.header, required this.lines});

  final String header;
  final List<String> lines;

  factory DiffHunkNode.fromJson(Map<String, dynamic> json) => DiffHunkNode(
    header: _string(json['header']),
    lines: [for (final item in _list(json['lines'])) _string(item)],
  );
}

final class DividerNode extends Node {
  const DividerNode({required this.label});

  @override
  String get kind => 'divider';

  final String? label;

  factory DividerNode.fromJson(Map<String, dynamic> json) =>
      DividerNode(label: _stringOrNull(json['label']));
}

final class DraftCardNode extends Node {
  const DraftCardNode({
    required this.id,
    required this.channel,
    required this.to,
    required this.body,
    required this.from,
    required this.subject,
    required this.status,
    required this.send,
    required this.discard,
  });

  @override
  String get kind => 'draftCard';

  final String id;
  final String channel;
  final String to;
  final String body;
  final String from;
  final String? subject;
  final String status;
  final ButtonNode? send;
  final ButtonNode? discard;

  factory DraftCardNode.fromJson(Map<String, dynamic> json) => DraftCardNode(
    id: _string(json['id']),
    channel: _string(json['channel']),
    to: _string(json['to']),
    body: _string(json['body']),
    from: _string(json['from']),
    subject: _stringOrNull(json['subject']),
    status: _string(json['status'], 'pending'),
    send: json['send'] is Map ? ButtonNode.fromJson(_map(json['send'])) : null,
    discard: json['discard'] is Map
        ? ButtonNode.fromJson(_map(json['discard']))
        : null,
  );
}

final class Emit {
  const Emit({required this.owner, required this.signal, required this.args});

  final String owner;
  final String signal;
  final Object? args;

  factory Emit.fromJson(Map<String, dynamic> json) => Emit(
    owner: _string(json['owner']),
    signal: _string(json['signal']),
    args: json['args'],
  );
}

final class EmptyStateNode extends Node {
  const EmptyStateNode({
    required this.title,
    required this.body,
    required this.suggestions,
  });

  @override
  String get kind => 'emptyState';

  final String title;
  final String? body;
  final List<ButtonNode>? suggestions;

  factory EmptyStateNode.fromJson(Map<String, dynamic> json) => EmptyStateNode(
    title: _string(json['title']),
    body: _stringOrNull(json['body']),
    suggestions: json['suggestions'] == null
        ? null
        : [
            for (final item in _list(json['suggestions']))
              ButtonNode.fromJson(_map(item)),
          ],
  );
}

final class Entry {
  const Entry({
    required this.id,
    required this.title,
    required this.subtitle,
    required this.trailing,
  });

  final String id;
  final String title;
  final String? subtitle;
  final String? trailing;

  factory Entry.fromJson(Map<String, dynamic> json) => Entry(
    id: _string(json['id']),
    title: _string(json['title']),
    subtitle: _stringOrNull(json['subtitle']),
    trailing: _stringOrNull(json['trailing']),
  );
}

final class ErrorLineNode extends Node {
  const ErrorLineNode({
    required this.what,
    required this.detail,
    required this.action,
  });

  @override
  String get kind => 'errorLine';

  final String what;
  final String? detail;
  final ButtonNode? action;

  factory ErrorLineNode.fromJson(Map<String, dynamic> json) => ErrorLineNode(
    what: _string(json['what']),
    detail: _stringOrNull(json['detail']),
    action: json['action'] is Map
        ? ButtonNode.fromJson(_map(json['action']))
        : null,
  );
}

final class FieldNode extends Node {
  const FieldNode({
    required this.key,
    required this.name,
    required this.fieldKind,
    required this.placeholder,
    required this.onSubmit,
  });

  @override
  String get kind => 'field';

  final String key;
  final String name;
  final FieldKind fieldKind;
  final String? placeholder;
  final Emit? onSubmit;

  factory FieldNode.fromJson(Map<String, dynamic> json) => FieldNode(
    key: _string(json['key']),
    name: _string(json['name']),
    fieldKind: _enum(FieldKind.values, json['fieldKind']),
    placeholder: _stringOrNull(json['placeholder']),
    onSubmit: json['onSubmit'] is Map
        ? Emit.fromJson(_map(json['onSubmit']))
        : null,
  );
}

final class GridNode extends Node {
  const GridNode({
    required this.columns,
    required this.children,
    required this.gap,
  });

  @override
  String get kind => 'grid';

  final int columns;
  final List<Node> children;
  final double gap;

  factory GridNode.fromJson(Map<String, dynamic> json) => GridNode(
    columns: _int(json['columns']),
    children: [
      for (final item in _list(json['children'])) Node.fromJson(_map(item)),
    ],
    gap: _double(json['gap'], 12.0),
  );
}

final class HeadingNode extends Node {
  const HeadingNode({
    required this.value,
    required this.level,
    required this.subtitle,
  });

  @override
  String get kind => 'heading';

  final String value;
  final int level;
  final String? subtitle;

  factory HeadingNode.fromJson(Map<String, dynamic> json) => HeadingNode(
    value: _string(json['value']),
    level: _int(json['level'], 2),
    subtitle: _stringOrNull(json['subtitle']),
  );
}

final class InfoBarNode extends Node {
  const InfoBarNode({
    required this.title,
    required this.message,
    required this.tone,
    required this.action,
  });

  @override
  String get kind => 'infoBar';

  final String title;
  final String message;
  final String tone;
  final ButtonNode? action;

  factory InfoBarNode.fromJson(Map<String, dynamic> json) => InfoBarNode(
    title: _string(json['title']),
    message: _string(json['message']),
    tone: _string(json['tone'], 'neutral'),
    action: json['action'] is Map
        ? ButtonNode.fromJson(_map(json['action']))
        : null,
  );
}

final class IntentNode extends Node {
  const IntentNode({required this.intentKind, required this.arg});

  @override
  String get kind => 'intent';

  final IntentKind intentKind;
  final String? arg;

  factory IntentNode.fromJson(Map<String, dynamic> json) => IntentNode(
    intentKind: _enum(IntentKind.values, json['intentKind']),
    arg: _stringOrNull(json['arg']),
  );
}

final class JobCardNode extends Node {
  const JobCardNode({
    required this.title,
    required this.steps,
    required this.progress,
    required this.status,
    required this.resultText,
    required this.cancel,
  });

  @override
  String get kind => 'jobCard';

  final String title;
  final List<JobStepNode> steps;
  final double progress;
  final String status;
  final String? resultText;
  final ButtonNode? cancel;

  factory JobCardNode.fromJson(Map<String, dynamic> json) => JobCardNode(
    title: _string(json['title']),
    steps: [
      for (final item in _list(json['steps'])) JobStepNode.fromJson(_map(item)),
    ],
    progress: _double(json['progress']),
    status: _string(json['status'], 'running'),
    resultText: _stringOrNull(json['resultText']),
    cancel: json['cancel'] is Map
        ? ButtonNode.fromJson(_map(json['cancel']))
        : null,
  );
}

final class JobStepNode {
  const JobStepNode({
    required this.label,
    required this.status,
    required this.detail,
  });

  final String label;
  final String status;
  final String? detail;

  factory JobStepNode.fromJson(Map<String, dynamic> json) => JobStepNode(
    label: _string(json['label']),
    status: _string(json['status'], 'waiting'),
    detail: _stringOrNull(json['detail']),
  );
}

final class MessageNode {
  const MessageNode({
    required this.id,
    required this.role,
    required this.text,
    required this.cards,
    required this.activities,
    required this.status,
    required this.model,
    required this.retry,
    required this.modelLabel,
    required this.providerIcon,
  });

  final String id;
  final String role;
  final String text;
  final List<Node>? cards;
  final List<ActivityNode>? activities;
  final String status;
  final String? model;
  final ButtonNode? retry;
  final String? modelLabel;
  final String? providerIcon;

  factory MessageNode.fromJson(Map<String, dynamic> json) => MessageNode(
    id: _string(json['id']),
    role: _string(json['role']),
    text: _string(json['text']),
    cards: json['cards'] == null
        ? null
        : [for (final item in _list(json['cards'])) Node.fromJson(_map(item))],
    activities: json['activities'] == null
        ? null
        : [
            for (final item in _list(json['activities']))
              ActivityNode.fromJson(_map(item)),
          ],
    status: _string(json['status'], 'completed'),
    model: _stringOrNull(json['model']),
    retry: json['retry'] is Map
        ? ButtonNode.fromJson(_map(json['retry']))
        : null,
    modelLabel: _stringOrNull(json['modelLabel']),
    providerIcon: _stringOrNull(json['providerIcon']),
  );
}

final class MetricTileNode extends Node {
  const MetricTileNode({
    required this.id,
    required this.appId,
    required this.title,
    required this.value,
    required this.delta,
    required this.deltaTone,
    required this.statusLabel,
    required this.statusTone,
    required this.sparkline,
    required this.threshold,
    required this.actions,
  });

  @override
  String get kind => 'metricTile';

  final String id;
  final String appId;
  final String title;
  final String value;
  final String? delta;
  final String deltaTone;
  final String? statusLabel;
  final String statusTone;
  final List<double>? sparkline;
  final double? threshold;
  final List<ButtonNode>? actions;

  factory MetricTileNode.fromJson(Map<String, dynamic> json) => MetricTileNode(
    id: _string(json['id']),
    appId: _string(json['appId']),
    title: _string(json['title']),
    value: _string(json['value']),
    delta: _stringOrNull(json['delta']),
    deltaTone: _string(json['deltaTone'], 'neutral'),
    statusLabel: _stringOrNull(json['statusLabel']),
    statusTone: _string(json['statusTone'], 'neutral'),
    sparkline: json['sparkline'] == null
        ? null
        : [for (final item in _list(json['sparkline'])) _double(item)],
    threshold: _doubleOrNull(json['threshold']),
    actions: json['actions'] == null
        ? null
        : [
            for (final item in _list(json['actions']))
              ButtonNode.fromJson(_map(item)),
          ],
  );
}

final class NavItemNode extends Node {
  const NavItemNode({
    required this.key,
    required this.label,
    required this.icon,
    required this.action,
    required this.selected,
    required this.badge,
    required this.emit,
  });

  @override
  String get kind => 'navItem';

  final String key;
  final String label;
  final String icon;
  final String action;
  final bool selected;
  final String? badge;
  final Emit? emit;

  factory NavItemNode.fromJson(Map<String, dynamic> json) => NavItemNode(
    key: _string(json['key']),
    label: _string(json['label']),
    icon: _string(json['icon']),
    action: _string(json['action']),
    selected: _bool(json['selected']),
    badge: _stringOrNull(json['badge']),
    emit: json['emit'] is Map ? Emit.fromJson(_map(json['emit'])) : null,
  );
}

final class NeuronRefNode extends Node {
  const NeuronRefNode({required this.refKind, required this.neuronUri});

  @override
  String get kind => 'neuronRef';

  final String refKind;
  final String neuronUri;

  factory NeuronRefNode.fromJson(Map<String, dynamic> json) => NeuronRefNode(
    refKind: _string(json['refKind']),
    neuronUri: _string(json['neuronUri']),
  );
}

final class ProgressNode extends Node {
  const ProgressNode({
    required this.label,
    required this.value,
    required this.detail,
  });

  @override
  String get kind => 'progress';

  final String label;
  final double value;
  final String? detail;

  factory ProgressNode.fromJson(Map<String, dynamic> json) => ProgressNode(
    label: _string(json['label']),
    value: _double(json['value']),
    detail: _stringOrNull(json['detail']),
  );
}

final class RecordCardNode extends Node {
  const RecordCardNode({
    required this.objectLabel,
    required this.name,
    required this.fields,
    required this.subtitle,
    required this.linkLabel,
    required this.open,
  });

  @override
  String get kind => 'recordCard';

  final String objectLabel;
  final String name;
  final List<RecordFieldNode> fields;
  final String? subtitle;
  final String linkLabel;
  final ButtonNode? open;

  factory RecordCardNode.fromJson(Map<String, dynamic> json) => RecordCardNode(
    objectLabel: _string(json['objectLabel']),
    name: _string(json['name']),
    fields: [
      for (final item in _list(json['fields']))
        RecordFieldNode.fromJson(_map(item)),
    ],
    subtitle: _stringOrNull(json['subtitle']),
    linkLabel: _string(json['linkLabel'], 'Open Neuron'),
    open: json['open'] is Map ? ButtonNode.fromJson(_map(json['open'])) : null,
  );
}

final class RecordFieldNode {
  const RecordFieldNode({
    required this.label,
    required this.value,
    required this.warn,
  });

  final String label;
  final String value;
  final bool warn;

  factory RecordFieldNode.fromJson(Map<String, dynamic> json) =>
      RecordFieldNode(
        label: _string(json['label']),
        value: _string(json['value']),
        warn: _bool(json['warn']),
      );
}

final class Resolve {
  const Resolve({required this.planId});

  final String planId;

  factory Resolve.fromJson(Map<String, dynamic> json) =>
      Resolve(planId: _string(json['planId']));
}

final class RowNode extends Node {
  const RowNode({
    required this.children,
    required this.gap,
    required this.padding,
    required this.extents,
  });

  @override
  String get kind => 'row';

  final List<Node> children;
  final double gap;
  final double padding;
  final List<double>? extents;

  factory RowNode.fromJson(Map<String, dynamic> json) => RowNode(
    children: [
      for (final item in _list(json['children'])) Node.fromJson(_map(item)),
    ],
    gap: _double(json['gap']),
    padding: _double(json['padding']),
    extents: json['extents'] == null
        ? null
        : [for (final item in _list(json['extents'])) _double(item)],
  );
}

final class SecretCardNode extends Node {
  const SecretCardNode({
    required this.name,
    required this.why,
    required this.savedIn,
    required this.status,
    required this.enter,
    required this.skip,
  });

  @override
  String get kind => 'secretCard';

  final String name;
  final String why;
  final String savedIn;
  final String status;
  final ButtonNode? enter;
  final ButtonNode? skip;

  factory SecretCardNode.fromJson(Map<String, dynamic> json) => SecretCardNode(
    name: _string(json['name']),
    why: _string(json['why']),
    savedIn: _string(json['savedIn'], 'Variables and secrets'),
    status: _string(json['status'], 'pending'),
    enter: json['enter'] is Map
        ? ButtonNode.fromJson(_map(json['enter']))
        : null,
    skip: json['skip'] is Map ? ButtonNode.fromJson(_map(json['skip'])) : null,
  );
}

final class SelectNode extends Node {
  const SelectNode({
    required this.label,
    required this.selected,
    required this.options,
  });

  @override
  String get kind => 'select';

  final String label;
  final String selected;
  final List<SelectOption> options;

  factory SelectNode.fromJson(Map<String, dynamic> json) => SelectNode(
    label: _string(json['label']),
    selected: _string(json['selected']),
    options: [
      for (final item in _list(json['options']))
        SelectOption.fromJson(_map(item)),
    ],
  );
}

final class SelectOption {
  const SelectOption({
    required this.key,
    required this.id,
    required this.label,
    required this.emit,
  });

  final String key;
  final String id;
  final String label;
  final Emit? emit;

  factory SelectOption.fromJson(Map<String, dynamic> json) => SelectOption(
    key: _string(json['key']),
    id: _string(json['id']),
    label: _string(json['label']),
    emit: json['emit'] is Map ? Emit.fromJson(_map(json['emit'])) : null,
  );
}

final class SpacerNode extends Node {
  const SpacerNode({required this.size});

  @override
  String get kind => 'spacer';

  final double size;

  factory SpacerNode.fromJson(Map<String, dynamic> json) =>
      SpacerNode(size: _double(json['size'], 12.0));
}

final class SplitPaneNode extends Node {
  const SplitPaneNode({
    required this.leading,
    required this.trailing,
    required this.leadingExtent,
    required this.gap,
  });

  @override
  String get kind => 'splitPane';

  final Node leading;
  final Node trailing;
  final double leadingExtent;
  final double gap;

  factory SplitPaneNode.fromJson(Map<String, dynamic> json) => SplitPaneNode(
    leading: Node.fromJson(_map(json['leading'])),
    trailing: Node.fromJson(_map(json['trailing'])),
    leadingExtent: _double(json['leadingExtent'], 280.0),
    gap: _double(json['gap'], 16.0),
  );
}

final class StatCardNode extends Node {
  const StatCardNode({
    required this.key,
    required this.label,
    required this.value,
    required this.delta,
    required this.tone,
    required this.icon,
  });

  @override
  String get kind => 'statCard';

  final String key;
  final String label;
  final String value;
  final String? delta;
  final BadgeTone tone;
  final String? icon;

  factory StatCardNode.fromJson(Map<String, dynamic> json) => StatCardNode(
    key: _string(json['key']),
    label: _string(json['label']),
    value: _string(json['value']),
    delta: _stringOrNull(json['delta']),
    tone: _enum(BadgeTone.values, json['tone']),
    icon: _stringOrNull(json['icon']),
  );
}

final class SurfaceNode extends Node {
  const SurfaceNode({
    required this.children,
    required this.surfaceKind,
    required this.title,
    required this.subtitle,
    required this.padding,
  });

  @override
  String get kind => 'surface';

  final List<Node> children;
  final String surfaceKind;
  final String? title;
  final String? subtitle;
  final double padding;

  factory SurfaceNode.fromJson(Map<String, dynamic> json) => SurfaceNode(
    children: [
      for (final item in _list(json['children'])) Node.fromJson(_map(item)),
    ],
    surfaceKind: _string(json['surfaceKind'], 'acrylic'),
    title: _stringOrNull(json['title']),
    subtitle: _stringOrNull(json['subtitle']),
    padding: _double(json['padding'], 18.0),
  );
}

final class SwitchNode extends Node {
  const SwitchNode({
    required this.key,
    required this.label,
    required this.isOn,
    required this.emit,
    required this.locked,
  });

  @override
  String get kind => 'switch';

  final String key;
  final String label;
  final bool isOn;
  final Emit? emit;
  final bool locked;

  factory SwitchNode.fromJson(Map<String, dynamic> json) => SwitchNode(
    key: _string(json['key']),
    label: _string(json['label']),
    isOn: _bool(json['isOn']),
    emit: json['emit'] is Map ? Emit.fromJson(_map(json['emit'])) : null,
    locked: _bool(json['locked']),
  );
}

final class TabItem {
  const TabItem({
    required this.key,
    required this.id,
    required this.label,
    required this.emit,
    required this.icon,
  });

  final String key;
  final String id;
  final String label;
  final Emit emit;
  final String? icon;

  factory TabItem.fromJson(Map<String, dynamic> json) => TabItem(
    key: _string(json['key']),
    id: _string(json['id']),
    label: _string(json['label']),
    emit: Emit.fromJson(_map(json['emit'])),
    icon: _stringOrNull(json['icon']),
  );
}

final class TabsNode extends Node {
  const TabsNode({required this.items, required this.selectedId});

  @override
  String get kind => 'tabs';

  final List<TabItem> items;
  final String selectedId;

  factory TabsNode.fromJson(Map<String, dynamic> json) => TabsNode(
    items: [
      for (final item in _list(json['items'])) TabItem.fromJson(_map(item)),
    ],
    selectedId: _string(json['selectedId']),
  );
}

final class TagPillNode extends Node {
  const TagPillNode({
    required this.label,
    required this.tone,
    required this.icon,
  });

  @override
  String get kind => 'tagPill';

  final String label;
  final String tone;
  final String? icon;

  factory TagPillNode.fromJson(Map<String, dynamic> json) => TagPillNode(
    label: _string(json['label']),
    tone: _string(json['tone'], 'neutral'),
    icon: _stringOrNull(json['icon']),
  );
}

final class TextNode extends Node {
  const TextNode({
    required this.value,
    required this.tone,
    required this.tabular,
    required this.mono,
    required this.size,
  });

  @override
  String get kind => 'text';

  final String value;
  final String tone;
  final bool tabular;
  final bool mono;
  final double? size;

  factory TextNode.fromJson(Map<String, dynamic> json) => TextNode(
    value: _string(json['value']),
    tone: _string(json['tone']),
    tabular: _bool(json['tabular']),
    mono: _bool(json['mono']),
    size: _doubleOrNull(json['size']),
  );
}

final class ToastNode extends Node {
  const ToastNode({required this.message});

  @override
  String get kind => 'toast';

  final String message;

  factory ToastNode.fromJson(Map<String, dynamic> json) =>
      ToastNode(message: _string(json['message']));
}

final class WebBrowserNode extends Node {
  const WebBrowserNode({
    required this.key,
    required this.url,
    required this.title,
    required this.height,
    required this.executionMode,
  });

  @override
  String get kind => 'webBrowser';

  final String key;
  final String url;
  final String? title;
  final double height;
  final String executionMode;

  factory WebBrowserNode.fromJson(Map<String, dynamic> json) => WebBrowserNode(
    key: _string(json['key']),
    url: _string(json['url']),
    title: _stringOrNull(json['title']),
    height: _double(json['height'], 520.0),
    executionMode: _string(json['executionMode'], 'wasm-ui'),
  );
}
