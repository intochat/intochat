import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';

/// Adapts a C# `ViewFixture` JSON map (`appId`, `scenario`, `side`, `main`) into
/// the surface descriptor that [ApplicationSurface.start] expects, and returns
/// an [IntoChatOSUiClient] that serves the mapped `UiNode` tree from `appRequest`.
({IntoChatOSUiClient client, Map<String, dynamic> surface})
surfaceClientFromFixture(Map<String, dynamic> fixture) {
  final appId = (fixture['appId'] as String? ?? 'app').trim();
  final viewName = 'local/apps/$appId/view';
  final root = viewFromFixture(fixture);
  final mockHttp = MockClient((request) async {
    if (request.url.path.endsWith('/node')) {
      return http.Response.bytes(
        utf8.encode(jsonEncode({'generation': 1, 'version': 1, 'root': root})),
        200,
        headers: {'content-type': 'application/json; charset=utf-8'},
      );
    }
    return http.Response.bytes(
      utf8.encode('{}'),
      200,
      headers: {'content-type': 'application/json; charset=utf-8'},
    );
  });
  return (
    client: IntoChatOSUiClient(
      baseUri: Uri.parse('http://localhost:8080/'),
      httpClient: mockHttp,
    ),
    surface: {'kind': 'view', 'name': viewName},
  );
}

/// Converts a C# `ViewFixture` JSON map into the wire `UiNode` tree that
/// `AppNeuron.MapView` serves on the HTTP app surface.
Map<String, dynamic> viewFromFixture(Map<String, dynamic> fixture) {
  final side = fixture['side'] as Map<String, dynamic>?;
  final main = fixture['main'] as Map<String, dynamic>;
  if (side == null) {
    return _mapView(main);
  }
  return _uiRow(
    'view',
    [
      _mapNode('side', side['root'] as Map<String, dynamic>),
      _mapNode('root', main['root'] as Map<String, dynamic>),
    ],
    gap: 12,
    extents: const [260, 0],
  );
}

Map<String, dynamic> _mapView(Map<String, dynamic> view) {
  final title = view['title'] as String? ?? '';
  final root = view['root'] as Map<String, dynamic>;
  final extents = root['extents'] as List?;
  if (root['kind'] == 'column' && extents != null && extents.isNotEmpty) {
    return _mapNode('view', root);
  }
  return _uiColumn('view', [_uiText('title', title), _mapNode('root', root)]);
}

Map<String, dynamic> _mapNode(String key, Map<String, dynamic> node) {
  final kind = node['kind'] as String? ?? '';
  switch (kind) {
    case 'column':
      final children = _mapList(node['children']);
      final extents = _numList(node['extents']);
      return _uiColumn(
        key,
        [
          for (var i = 0; i < children.length; i++)
            _mapNode('$key.$i', children[i]),
        ],
        gap: _maxZero(node['gap']),
        extents: extents != null && extents.length == children.length
            ? extents
            : null,
      );

    case 'row':
      final children = _mapList(node['children']);
      final extents = _numList(node['extents']);
      return _uiRow(
        key,
        [
          for (var i = 0; i < children.length; i++)
            _mapNode('$key.$i', children[i]),
        ],
        gap: _maxZero(node['gap']),
        extents: extents != null && extents.length == children.length
            ? extents
            : null,
      );

    case 'grid':
      final children = _mapList(node['children']);
      final cols = (node['columns'] as num?)?.toInt() ?? 1;
      return _metaCard(
        key,
        '',
        {
          '_ui': 'grid',
          'columns': cols < 1 ? 1 : cols,
          'gap': _maxZero(node['gap']),
        },
        [
          for (var i = 0; i < children.length; i++)
            _mapNode('$key.$i', children[i]),
        ],
      );

    case 'splitPane':
      final leading = node['leading'] as Map<String, dynamic>;
      final trailing = node['trailing'] as Map<String, dynamic>;
      return _uiRow(
        key,
        [_mapNode('$key.0', leading), _mapNode('$key.1', trailing)],
        gap: _maxZero(node['gap']),
        extents: [_maxZero(node['leadingExtent']), 0],
      );

    case 'surface':
      final children = _mapList(node['children']);
      final surfaceKind = (node['surfaceKind'] as String? ?? 'acrylic')
          .toLowerCase();
      return _metaCard(
        key,
        node['title'] as String? ?? '',
        {
          '_ui': 'surface',
          'kind': surfaceKind,
          'subtitle': node['subtitle'],
          'gap': 12.0,
          'padding': _maxZero(node['padding']),
        },
        [
          for (var i = 0; i < children.length; i++)
            _mapNode('$key.$i', children[i]),
        ],
      );

    case 'tabs':
      final items = _mapList(node['items']);
      if (items.isEmpty) return _uiColumn(key, const []);
      final selectedId = node['selectedId'] as String?;
      var activeIndex = items.indexWhere((item) => item['id'] == selectedId);
      if (activeIndex < 0) activeIndex = 0;
      return {
        'kind': 13,
        'key': key,
        'children': [
          for (var i = 0; i < items.length; i++)
            _uiTab(
              '$key.tab.$i',
              _nonEmpty(
                items[i]['label'] as String?,
                items[i]['id'] as String? ?? '',
              ),
              _formatAction(items[i]['action'] as Map<String, dynamic>?),
              i == activeIndex,
            ),
        ],
      };

    case 'divider':
      return {'kind': 12, 'key': key, 'text': node['label']};

    case 'spacer':
      return _metaCard(key, '', {
        '_ui': 'spacer',
        'size': _maxZero(node['size']),
      });

    case 'collection':
      final entries = _mapList(node['entries']);
      return {
        'kind': 9,
        'key': key,
        'children': [
          for (var i = 0; i < entries.length; i++)
            _uiCard(
              '$key.entry.$i.${entries[i]['id'] ?? i}',
              entries[i]['title'] as String? ?? '',
              entries[i]['subtitle'] as String?,
            ),
        ],
      };

    case 'heading':
      return {
        'kind': 0,
        'key': key,
        'text': node['value'] as String? ?? '',
        'detail': jsonEncode({
          '_ui': 'heading',
          'level': node['level'] ?? 2,
          'subtitle': node['subtitle'],
        }),
      };

    case 'text':
      final tone = (node['tone'] as String? ?? '').trim();
      final tabular = node['tabular'] == true;
      final mono = node['mono'] == true;
      final size = node['size'];
      final val = node['value'] as String? ?? '';
      if ((tone.isEmpty || tone == 'neutral' || tone == 'default') &&
          !tabular &&
          !mono &&
          size == null) {
        return _uiText(key, val);
      }
      return {
        'kind': 0,
        'key': key,
        'text': val,
        'detail': jsonEncode({
          '_ui': 'text',
          'tone': tone.toLowerCase(),
          'tabular': tabular,
          'mono': mono,
          'size': size,
        }),
      };

    case 'badge':
      return {
        'kind': 10,
        'key': key,
        'text': _nonEmpty(node['label'] as String?, '•'),
        'tone': _mapToneString(node['tone'] as String?),
        'icon': _sanitizeIcon(node['icon'] as String?),
      };

    case 'tagPill':
      final tone = (node['tone'] as String? ?? 'neutral');
      return {
        'kind': 10,
        'key': key,
        'text': _nonEmpty(node['label'] as String?, '•'),
        'tone': _mapToneString(tone),
        'icon': _sanitizeIcon(node['icon'] as String?),
        'detail': jsonEncode({'_ui': 'tagpill', 'tone': tone.toLowerCase()}),
      };

    case 'button':
      final action = node['action'] as Map<String, dynamic>?;
      final op = action?['operation'] as String?;
      return {
        'kind': 1,
        'key': key,
        'text': _nonEmpty(node['label'] as String?, _nonEmpty(op, 'Action')),
        'action': _formatAction(action),
        'enabled': node['enabled'] != false,
        'style': _mapButtonStyle(node),
        'icon': _sanitizeIcon(node['icon'] as String?),
        'detail': node['small'] == true ? 'small' : null,
      };

    case 'textField':
      final name = node['name'] as String? ?? '';
      final val = node['value'] as String?;
      final placeholder = node['placeholder'] as String?;
      return _metaCard(
        key,
        val ?? placeholder ?? name,
        {
          '_ui': 'textfield',
          'name': name,
          'value': val,
          'placeholder': placeholder,
          'secret': false,
        },
        [
          ?_actionButton(
            '$key.submit',
            _nonEmpty(name, 'Submit'),
            node['onSubmit'] as Map<String, dynamic>?,
            style: 0,
          ),
        ],
      );

    case 'switch':
      final label = node['label'] as String? ?? '';
      final swAction = node['action'] as Map<String, dynamic>?;
      final locked = node['locked'] == true;
      return _metaCard(
        key,
        label,
        {
          '_ui': 'switch',
          'label': label,
          'value': node['isOn'] == true,
          'enabled': swAction != null,
          'locked': locked,
        },
        [
          if (swAction != null)
            _uiButton(
              '$key.toggle',
              _nonEmpty(label, 'Toggle'),
              _formatAction(swAction),
              enabled: !locked,
            ),
        ],
      );

    case 'select':
      final label = node['label'] as String? ?? '';
      final options = _mapList(node['options']);
      return _metaCard(
        key,
        label,
        {
          '_ui': 'select',
          'label': label,
          'selectedId': node['selected'],
          'enabled': true,
          'options': [
            for (final o in options) {'id': o['id'], 'label': o['label']},
          ],
        },
        [
          for (var i = 0; i < options.length; i++)
            _uiButton(
              '$key.opt.$i',
              _nonEmpty(
                options[i]['label'] as String?,
                options[i]['id'] as String? ?? '',
              ),
              _formatAction(
                options[i]['action'] as Map<String, dynamic>? ??
                    {
                      'neuron': 'app',
                      'operation': 'select',
                      'arguments': [options[i]['id'] ?? ''],
                    },
              ),
            ),
        ],
      );

    case 'progress':
      final label = node['label'] as String? ?? '';
      return _metaCard(key, label, {
        '_ui': 'progress',
        'value': node['value'] ?? 0,
        'label': label,
        'detail': node['detail'],
        'indeterminate': false,
      });

    case 'infoBar':
      final title = node['title'] as String? ?? '';
      final actionNode = node['action'] as Map<String, dynamic>?;
      return _metaCard(
        key,
        title,
        {
          '_ui': 'infobar',
          'title': title,
          'body': node['message'] as String? ?? '',
          'tone': (node['tone'] as String? ?? 'info').toLowerCase(),
        },
        [if (actionNode != null) _mapNode('$key.action', actionNode)],
      );

    case 'toast':
      return {'kind': 8, 'key': key, 'text': node['message'] as String? ?? ''};

    case 'card':
      final children = _mapList(node['children']);
      return _uiCard(
        key,
        node['title'] as String? ?? '',
        node['body'] as String?,
        [
          for (var i = 0; i < children.length; i++)
            _mapNode('$key.$i', children[i]),
        ],
      );

    case 'dataTable':
      final title = node['title'] as String? ?? '';
      final sourceUri = node['sourceNeuronUri'] as String?;
      return _metaCard(
        key,
        title,
        {
          '_ui': 'datatable',
          'title': title,
          'columns': node['columns'] ?? const [],
          'rows': node['rows'] ?? const [],
          'sourceNeuronUri': sourceUri,
          'from': node['from'],
        },
        [?_copyUriButton('$key.copy', sourceUri)],
      );

    case 'chart':
      final title = node['title'] as String? ?? '';
      final sourceUri = node['sourceNeuronUri'] as String?;
      final points = _mapList(node['points']);
      final comparePoints = (node['comparePoints'] as List?)
          ?.whereType<num>()
          .toList();
      return _metaCard(
        key,
        title,
        {
          '_ui': 'chart',
          'kind': node['chartKind'] ?? node['kind'] ?? 'bar',
          'title': title,
          'subtitle': node['from'],
          'unit': node['prefix'],
          'seriesName': node['seriesName'],
          'compareName': node['compareName'],
          'sourceNeuronUri': sourceUri,
          'points': [
            for (var i = 0; i < points.length; i++)
              {
                'id': 'pt-$i',
                'label': points[i]['label'],
                'value': points[i]['value'],
              },
          ],
          'comparePoints': comparePoints != null
              ? [
                  for (var i = 0; i < comparePoints.length; i++)
                    {
                      'id': 'cmp-$i',
                      'label': i < points.length ? points[i]['label'] : 'pt-$i',
                      'value': comparePoints[i],
                    },
                ]
              : null,
        },
        [?_copyUriButton('$key.copy', sourceUri)],
      );

    case 'approvalCard':
      final title = node['title'] as String? ?? '';
      final sourceUri = node['sourceNeuronUri'] as String?;
      final rows = _mapList(node['rows']);
      return _metaCard(
        key,
        title,
        {
          '_ui': 'approval',
          'id': node['id'],
          'verb': title,
          'target': '',
          'rows': [
            for (final r in rows)
              {
                'key': r['label'],
                'value': r['newValue'],
                'oldValue': r['oldValue'],
                'amount': r['amount'],
              },
          ],
          'sourceApp': node['systemName'],
          'sourceNeuronUri': sourceUri,
          'scopeUri': node['summary'],
          'state': node['status'],
          'approveLabel': 'Approve',
          'rejectLabel': 'Deny',
          'rememberHint': node['summary'],
        },
        [
          ?_actionButton(
            '$key.approve',
            'Approve',
            node['onApprove'] as Map<String, dynamic>?,
            style: 0,
          ),
          ?_actionButton(
            '$key.reject',
            'Deny',
            node['onReject'] as Map<String, dynamic>?,
            style: 1,
          ),
          ?_copyUriButton('$key.copy', sourceUri),
        ],
      );

    case 'choiceCard':
      final question = node['question'] as String? ?? '';
      final options = _mapList(node['options']);
      return _metaCard(
        key,
        question,
        {
          '_ui': 'choice',
          'question': question,
          'selectedIndex': node['pickedIndex'],
          'sourceApp': 'Ino',
          'allowOther': true,
          'options': [
            for (final o in options) {'label': o['title'], 'note': o['detail']},
          ],
        },
        [
          for (var i = 0; i < options.length; i++)
            _uiButton(
              '$key.opt.$i',
              _nonEmpty(options[i]['title'] as String?, 'Option ${i + 1}'),
              _formatAction(
                options[i]['action'] as Map<String, dynamic>? ??
                    {
                      'neuron': 'app',
                      'operation': 'choose',
                      'arguments': ['$i'],
                    },
              ),
            ),
        ],
      );

    case 'draftCard':
      final subject = node['subject'] as String?;
      final to = node['to'] as String? ?? '';
      return _metaCard(
        key,
        subject ?? to,
        {
          '_ui': 'draft',
          'channel': node['channel'],
          'recipient': to,
          'subject': subject,
          'body': node['body'],
          'sent': (node['status'] as String? ?? '').toLowerCase() == 'sent',
          'sendLabel': 'Send',
        },
        [
          ?_actionButton(
            '$key.send',
            'Send',
            node['onSend'] as Map<String, dynamic>?,
            style: 0,
          ),
          ?_actionButton(
            '$key.cancel',
            'Discard',
            node['onDiscard'] as Map<String, dynamic>?,
            style: 2,
          ),
        ],
      );

    case 'recordCard':
      final name = node['name'] as String? ?? '';
      final fields = _mapList(node['fields']);
      final linkLabel = node['linkLabel'] as String?;
      return _metaCard(
        key,
        name,
        {
          '_ui': 'record',
          'kind': node['objectLabel'],
          'title': name,
          'subtitle': node['subtitle'],
          'linkLabel': linkLabel,
          'status': 'ready',
          'statusTone': 'accent',
          'sourceApp': node['objectLabel'],
          'fields': [
            for (final f in fields) {'key': f['label'], 'value': f['value']},
          ],
        },
        [
          ?_actionButton(
            '$key.open',
            _nonEmpty(linkLabel, 'Open'),
            node['onOpen'] as Map<String, dynamic>?,
            style: 1,
          ),
        ],
      );

    case 'secretCard':
      final name = node['name'] as String? ?? '';
      return _metaCard(
        key,
        name,
        {
          '_ui': 'secret',
          'service': name,
          'keyName': node['savedIn'],
          'hint': node['why'],
          'saved': (node['status'] as String? ?? '').toLowerCase() == 'saved',
        },
        [
          ?_actionButton(
            '$key.save',
            'Save to vault',
            node['onEnter'] as Map<String, dynamic>?,
            style: 0,
          ),
          ?_actionButton(
            '$key.skip',
            'Skip',
            node['onSkip'] as Map<String, dynamic>?,
            style: 2,
          ),
        ],
      );

    case 'jobCard':
      final title = node['title'] as String? ?? '';
      final steps = _mapList(node['steps']);
      var currentStep = steps.indexWhere(
        (s) => (s['status'] as String? ?? '').toLowerCase() == 'running',
      );
      if (currentStep < 0) currentStep = 0;
      return _metaCard(
        key,
        title,
        {
          '_ui': 'job',
          'title': title,
          'currentStep': currentStep,
          'progress': node['progress'],
          'eta': node['resultText'],
          'done': (node['status'] as String? ?? '').toLowerCase() == 'done',
          'steps': [
            for (final s in steps) {'label': s['label'], 'detail': s['detail']},
          ],
        },
        [
          ?_actionButton(
            '$key.cancel',
            'Cancel',
            node['onCancel'] as Map<String, dynamic>?,
            style: 2,
          ),
        ],
      );

    case 'brainScene':
      final nodes = _mapList(node['nodes']);
      final synapses = _mapList(node['synapses']);
      return _metaCard(
        key,
        'Brain Scene 3D',
        {
          '_ui': 'brain3d',
          'selectedId': node['selectedId'],
          'pauseRotation': node['pauseRotation'] == true,
          'zoomTargetId': node['zoomTargetId'],
          'targetZoom': node['targetZoom'],
          'height': 340.0,
          'interactive': true,
          'showHud': true,
          'nodes': [
            for (final n in nodes)
              {
                'id': n['id'],
                'label': n['label'],
                'category': n['kind'],
                'uri': n['uri'],
                'subtitle': n['subtitle'] ?? n['uri'],
                'status': n['status'],
                'x': n['x'],
                'y': n['y'],
                'z': n['z'],
              },
          ],
          'synapses': [
            for (final s in synapses)
              {
                'fromId': s['fromId'],
                'toId': s['toId'],
                'active': s['active'],
                'signalType': s['signalLabel'] ?? 'Signal',
              },
          ],
        },
        [
          for (var i = 0; i < nodes.length; i++)
            _uiButton(
              '$key.node.$i',
              _nonEmpty(
                nodes[i]['label'] as String?,
                _nonEmpty(nodes[i]['id'] as String?, 'Node ${i + 1}'),
              ),
              _formatAction(
                nodes[i]['onSelect'] as Map<String, dynamic>? ??
                    {
                      'neuron': 'app',
                      'operation': 'inspect',
                      'arguments': [nodes[i]['id'] ?? ''],
                    },
              ),
            ),
        ],
      );

    case 'neuronRef':
      final uri = node['neuronUri'] as String? ?? '';
      return _metaCard(
        key,
        uri,
        {
          '_ui': 'neuronRef',
          'kind': node['refKind'] ?? node['kind'],
          'neuronUri': uri,
          'title': uri,
        },
        [?_copyUriButton('$key.copy', uri)],
      );

    case 'appShell':
      final shellKey = _nonEmpty(node['key'] as String?, key);
      final sidebar = node['sidebar'] as Map<String, dynamic>?;
      final toolbar = node['toolbar'] as Map<String, dynamic>?;
      final body = node['body'] as Map<String, dynamic>;
      final inspector = node['inspector'] as Map<String, dynamic>?;
      return _metaCard(
        shellKey,
        '',
        {
          '_ui': 'appshell',
          'hasSidebar': sidebar != null,
          'hasToolbar': toolbar != null,
          'hasInspector': inspector != null,
        },
        [
          if (sidebar != null) _mapNode('$shellKey.sidebar', sidebar),
          if (toolbar != null) _mapNode('$shellKey.toolbar', toolbar),
          _mapNode('$shellKey.body', body),
          if (inspector != null) _mapNode('$shellKey.inspector', inspector),
        ],
      );

    case 'navItem':
      final navKey = _nonEmpty(node['key'] as String?, key);
      final label = node['label'] as String? ?? '';
      final selected = node['selected'] == true;
      final rawIcon = node['icon'] as String?;
      return _metaCard(
        navKey,
        label,
        {
          '_ui': 'navitem',
          'label': label,
          'icon': _sanitizeIcon(rawIcon) ?? rawIcon,
          'selected': selected,
          'badge': node['badge'],
        },
        [
          _uiButton(
            '$navKey.act',
            _nonEmpty(label, 'Select'),
            _nonEmpty(node['action'] as String?, 'noop'),
            style: selected ? 0 : 2,
          ),
        ],
      );

    case 'statCard':
      final statKey = _nonEmpty(node['key'] as String?, key);
      final label = node['label'] as String? ?? '';
      final rawIcon = node['icon'] as String?;
      final toneStr = switch (node['tone']) {
        0 => 'neutral',
        1 => 'accent',
        2 => 'done',
        3 => 'working',
        4 => 'error',
        final String s => s.toLowerCase(),
        _ => 'neutral',
      };
      return _metaCard(statKey, label, {
        '_ui': 'statcard',
        'id': statKey,
        'label': label,
        'value': node['value'] as String? ?? '0',
        'delta': node['delta'],
        'tone': toneStr,
        'icon': _sanitizeIcon(rawIcon) ?? rawIcon,
      });

    case 'webBrowser':
      final wbKey = _nonEmpty(node['key'] as String?, key);
      final title = _nonEmpty(node['title'] as String?, 'WASM Studio');
      return _metaCard(wbKey, title, {
        '_ui': 'webbrowser',
        'url': node['url'],
        'title': title,
        'height': node['height'] ?? 0,
        'executionMode': node['executionMode'] ?? 'wasm-ui',
      });

    default:
      return _uiText(key, key);
  }
}

List<Map<String, dynamic>> _mapList(Object? value) =>
    (value as List? ?? const [])
        .whereType<Map>()
        .map(Map<String, dynamic>.from)
        .toList();

List<num>? _numList(Object? value) {
  final list = (value as List?)?.whereType<num>().toList();
  return list == null || list.isEmpty ? null : list;
}

num _maxZero(Object? value) {
  final n = value is num ? value : 0;
  return n < 0 ? 0 : n;
}

String _nonEmpty(String? value, String fallback) =>
    value == null || value.trim().isEmpty ? fallback : value;

Map<String, dynamic> _uiText(String key, String text) => {
  'kind': 0,
  'key': key,
  'text': text,
};

Map<String, dynamic> _uiButton(
  String key,
  String text,
  String action, {
  bool enabled = true,
  int style = 1,
  String? icon,
}) => {
  'kind': 1,
  'key': key,
  'text': text,
  'action': action,
  'enabled': enabled,
  'style': style,
  'icon': ?icon,
};

Map<String, dynamic> _uiTab(
  String key,
  String text,
  String action,
  bool active,
) => _uiButton(key, text, action, style: active ? 0 : 2);

Map<String, dynamic> _uiColumn(
  String key,
  List<Map<String, dynamic>> children, {
  num gap = 0,
  List<num>? extents,
}) => {
  'kind': 2,
  'key': key,
  'children': children,
  'gap': gap,
  'extents': ?extents,
};

Map<String, dynamic> _uiRow(
  String key,
  List<Map<String, dynamic>> children, {
  num gap = 0,
  List<num>? extents,
}) => {
  'kind': 3,
  'key': key,
  'children': children,
  'gap': gap,
  'extents': ?extents,
};

Map<String, dynamic> _uiCard(
  String key,
  String title,
  String? detail, [
  List<Map<String, dynamic>> children = const [],
]) => {
  'kind': 4,
  'key': key,
  'text': title,
  'detail': ?detail,
  'children': children,
};

Map<String, dynamic> _metaCard(
  String key,
  String title,
  Map<String, dynamic> meta, [
  List<Map<String, dynamic>> children = const [],
]) => _uiCard(key, title, jsonEncode(meta), children);

Map<String, dynamic>? _actionButton(
  String key,
  String label,
  Map<String, dynamic>? action, {
  int style = 1,
}) => action == null
    ? null
    : _uiButton(key, label, _formatAction(action), style: style);

Map<String, dynamic>? _copyUriButton(String key, String? uri) =>
    uri == null || uri.trim().isEmpty
    ? null
    : _uiButton(key, 'Copy URI', 'copy:$uri', icon: 'copy');

String _formatAction(Map<String, dynamic>? action) {
  if (action == null) return 'noop';
  final neuron = action['neuron'] as String? ?? '';
  final operation = action['operation'] as String? ?? '';
  final args = (action['arguments'] as List? ?? const [])
      .map((a) => '$a')
      .toList();
  if (operation.toLowerCase() == 'copy' && args.isNotEmpty) {
    return 'copy:${args[0]}';
  }
  if (neuron.toLowerCase().startsWith('os://')) return neuron;
  if (neuron.toLowerCase().startsWith('apps/') &&
      operation.toLowerCase() == 'open') {
    return 'os://window/open?app=${neuron.substring(5)}';
  }
  final op = _nonEmpty(operation, 'noop');
  return args.isNotEmpty
      ? '$op:${args.map(Uri.encodeComponent).join(':')}'
      : op;
}

int _mapButtonStyle(Map<String, dynamic> button) {
  final style = (button['style'] as String? ?? '').trim().toLowerCase();
  final tone = (button['tone'] as String? ?? '').trim().toLowerCase();
  if (tone == 'danger' || style == 'danger') return 3;
  if (style == 'primary' || tone == 'accent') return 0;
  return switch (style) {
    'ghost' => 2,
    'link' => 4,
    _ => 1,
  };
}

int _mapToneString(String? tone) => switch ((tone ?? '').trim().toLowerCase()) {
  'done' || 'ok' || 'success' => 1,
  'working' || 'warn' || 'warning' || 'accent' || 'info' => 2,
  'error' || 'danger' => 3,
  _ => 0,
};

String? _sanitizeIcon(String? icon) {
  if (icon == null || icon.trim().isEmpty) return null;
  final buf = StringBuffer();
  for (final code in icon.trim().toLowerCase().runes.take(64)) {
    final ch = String.fromCharCode(code);
    if (ch == '_' || ch == ' ') {
      buf.write('-');
    } else if (RegExp(r'[a-z0-9\-]').hasMatch(ch)) {
      buf.write(ch);
    }
  }
  final result = buf.toString().replaceAll(RegExp(r'^-+|-+$'), '');
  return result.isEmpty ? null : result;
}
