part of 'neuron_view.dart';

/// Inline presentation values never acquire their own load or watch lifecycle.
class _ImmutableView extends StatelessWidget {
  const _ImmutableView({
    required this.name,
    required this.generation,
    required this.root,
    required this.enabled,
    required this.dispatch,
    required this.markdown,
    required this.reference,
    required this.layout,
    this.localPreferences,
    this.onActivate,
  });
  final String name;
  final int generation;
  final Map root;
  final bool enabled;
  final Future<void> Function(Map<String, dynamic>) dispatch;
  final Widget Function(String) markdown;
  final Widget Function(Map, Key) reference;
  final Widget Function(Map<String, dynamic>, List<Widget>, List) layout;
  final LocalPreferences? localPreferences;
  final ValueChanged<Map<String, dynamic>>? onActivate;

  static String _operationId() => const Uuid().v4();

  static Map<String, dynamic>? _tryParseUiMeta(Object? detail) {
    if (detail is! String || !detail.startsWith('{"_ui":')) return null;
    try {
      final decoded = jsonDecode(detail);
      return decoded is Map ? Map<String, dynamic>.from(decoded) : null;
    } catch (_) {
      return null;
    }
  }

  static CardSource _cardSource(BuildContext context, String sourceName) =>
      CardSource(
        sourceName,
        Icon(Icons.auto_awesome, size: 14, color: UiColors.of(context).accent),
      );

  /// The one button dispatch for every kind; null when it cannot act.
  /// A `copy:` action is handled here: it copies its payload and dispatches nothing.
  VoidCallback? _dispatchButton(BuildContext context, Map node) =>
      enabled && node['enabled'] != false && node['action'] is String
      ? () {
          FocusManager.instance.primaryFocus?.unfocus();
          final action = node['action'] as String;
          if (action.startsWith('copy:')) {
            unawaited(
              Clipboard.setData(ClipboardData(text: action.substring(5))).then(
                (_) {
                  if (context.mounted) showKitToast(context, 'Link copied.');
                },
                onError: (Object _) {
                  if (context.mounted) {
                    showKitToast(
                      context,
                      'Could not copy the link.',
                      tone: UiTone.error,
                    );
                  }
                },
              ),
            );
            return;
          }
          if (action.startsWith('os://')) {
            final uri = Uri.tryParse(action);
            if (onActivate != null) {
              onActivate!({
                'kind': 'os_uri',
                'uri': action,
                'host': uri?.host ?? '',
                'target': uri?.pathSegments.firstOrNull ?? '',
                'query': uri?.queryParameters ?? const <String, String>{},
              });
              return;
            }
          }
          if (action.endsWith(':open-settings') && onActivate != null) {
            onActivate!({
              'kind': 'os_uri',
              'uri': 'os://window/open?app=settings&section=connections',
              'host': 'window',
              'target': 'open',
              'query': const {'app': 'settings', 'section': 'connections'},
            });
          }
          unawaited(
            dispatch({
              'kind': 'view',
              'name': name,
              'revision': generation,
              'field': node['key'],
              'action': node['action'],
              'operationId': _operationId(),
            }).catchError((Object _) {}),
          );
        }
      : null;

  Map? _findChildByKeySuffix(List<Map> children, String suffix) {
    for (final child in children) {
      final k = child['key'];
      if (k is String && k.endsWith(suffix)) return child;
    }
    return null;
  }

  VoidCallback? _childAction(
    BuildContext context,
    List<Map> children,
    String suffix,
  ) {
    final child = _findChildByKeySuffix(children, suffix);
    return child != null ? _dispatchButton(context, child) : null;
  }

  Widget _buildRichCard(
    BuildContext context,
    String text,
    Map<String, dynamic> meta,
    List<Map> children,
    List<Widget> widgets,
  ) {
    final uiKind = meta['_ui'] as String? ?? '';
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);

    switch (uiKind) {
      case 'spacer':
        final size = (meta['size'] as num?)?.toDouble() ?? 12.0;
        return SizedBox(height: size, width: size);

      case 'appshell':
        var idx = 0;
        final sidebarWidget = meta['hasSidebar'] == true && idx < widgets.length
            ? widgets[idx++]
            : null;
        final toolbarWidget = meta['hasToolbar'] == true && idx < widgets.length
            ? widgets[idx++]
            : null;
        final bodyWidget = idx < widgets.length
            ? widgets[idx++]
            : const SizedBox.shrink();
        final inspectorWidget =
            meta['hasInspector'] == true && idx < widgets.length
            ? widgets[idx++]
            : null;

        final mainColumn = Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (toolbarWidget != null) ...[
              IcAcrylicBox(
                padding: const EdgeInsets.symmetric(
                  horizontal: 14,
                  vertical: 10,
                ),
                borderRadius: BorderRadius.circular(IcRadius.card),
                child: toolbarWidget,
              ),
              const SizedBox(height: 12),
            ],
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(child: bodyWidget),
                if (inspectorWidget != null) ...[
                  const SizedBox(width: 14),
                  SizedBox(
                    width: 280,
                    child: IcAcrylicBox(
                      padding: const EdgeInsets.all(14),
                      borderRadius: BorderRadius.circular(IcRadius.card),
                      child: inspectorWidget,
                    ),
                  ),
                ],
              ],
            ),
          ],
        );

        if (sidebarWidget == null) return mainColumn;
        final sidebarCard = IcAcrylicBox(
          padding: const EdgeInsets.all(12),
          borderRadius: BorderRadius.circular(IcRadius.card),
          child: sidebarWidget,
        );
        return LayoutBuilder(
          builder: (context, constraints) => constraints.maxWidth < 680
              ? Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  spacing: 12,
                  children: [sidebarCard, mainColumn],
                )
              : Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    SizedBox(width: 220, child: sidebarCard),
                    const SizedBox(width: 14),
                    Expanded(child: mainColumn),
                  ],
                ),
        );

      case 'navitem':
        final label = meta['label'] as String? ?? text;
        final iconSlug = meta['icon'] as String?;
        final selected = meta['selected'] == true;
        final badge = meta['badge'] as String?;
        final actChild =
            _findChildByKeySuffix(children, '.act') ??
            (children.isNotEmpty ? children.first : null);
        return Row(
          children: [
            Expanded(
              child: Btn(
                label: label,
                primary: selected,
                small: true,
                icon: iconSlug != null ? uiIcon(iconSlug) : null,
                onTap: actChild != null
                    ? _dispatchButton(context, actChild)
                    : null,
              ),
            ),
            if (badge != null && badge.isNotEmpty) ...[
              const SizedBox(width: 6),
              TagPill(
                label: badge,
                tone: selected ? TagTone.accent : TagTone.neutral,
              ),
            ],
          ],
        );

      case 'statcard':
        final label = meta['label'] as String? ?? text;
        final value = meta['value'] as String? ?? '0';
        final delta = meta['delta'] as String?;
        final iconSlug = meta['icon'] as String?;
        final tagTone = switch ((meta['tone'] as String? ?? 'neutral')
            .toLowerCase()) {
          'accent' => TagTone.accent,
          'done' => TagTone.done,
          'working' => TagTone.working,
          'error' => TagTone.error,
          _ => TagTone.neutral,
        };
        final ic = IcTheme.of(context);
        return IcAcrylicBox(
          padding: const EdgeInsets.all(14),
          borderRadius: BorderRadius.circular(IcRadius.card),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  if (iconSlug != null && uiIcon(iconSlug) != null) ...[
                    Icon(uiIcon(iconSlug), size: 14, color: ic.textMuted),
                    const SizedBox(width: 6),
                  ],
                  Expanded(
                    child: Text(
                      label,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        12,
                        weight: FontWeight.w500,
                        color: ic.textMuted,
                      ),
                    ),
                  ),
                  if (delta != null && delta.isNotEmpty)
                    TagPill(label: delta, tone: tagTone),
                ],
              ),
              const SizedBox(height: 8),
              Text(
                value,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: IcType.text(
                  22,
                  weight: FontWeight.w700,
                  color: ic.textStrong,
                  tnum: true,
                ),
              ),
            ],
          ),
        );

      case 'grid':
        final cols = ((meta['columns'] as num?)?.toInt() ?? 2).clamp(1, 12);
        final gap = (meta['gap'] as num?)?.toDouble() ?? 12.0;
        if (widgets.isEmpty) return const SizedBox.shrink();
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: gap,
          children: [
            for (var i = 0; i < widgets.length; i += cols)
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                spacing: gap,
                children: [
                  for (var col = 0; col < cols; col++)
                    Expanded(
                      child: i + col < widgets.length
                          ? widgets[i + col]
                          : const SizedBox.shrink(),
                    ),
                ],
              ),
          ],
        );

      case 'surface':
        final surfaceKind = meta['kind'] as String? ?? 'acrylic';
        final gap = (meta['gap'] as num?)?.toDouble() ?? 12.0;
        final pad = (meta['padding'] as num?)?.toDouble() ?? 16.0;
        final inner = Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: gap,
          children: [
            if (text.isNotEmpty)
              Text(
                text,
                style: UiType.displayStyle(
                  15,
                  weight: FontWeight.w600,
                  color: c.strong,
                ),
              ),
            ...widgets,
          ],
        );
        return surfaceKind == 'recessed'
            ? Container(
                width: double.infinity,
                padding: EdgeInsets.all(pad),
                decoration: BoxDecoration(
                  color: c.recessed,
                  borderRadius: BorderRadius.circular(tokens.card),
                  border: Border.all(color: c.hairline),
                ),
                child: inner,
              )
            : SizedBox(
                width: double.infinity,
                child: UiKitSurface(padding: EdgeInsets.all(pad), child: inner),
              );

      case 'textfield':
        final fieldName = meta['name'] as String? ?? '';
        return _ImmutableFieldBox(
          label: fieldName,
          value: meta['value'] as String? ?? '',
          placeholder: meta['placeholder'] as String? ?? fieldName,
          secret: meta['secret'] == true,
          enabled: enabled,
          onSubmit: _childAction(context, children, '.submit'),
        );

      case 'switch':
        final label = meta['label'] as String? ?? text;
        final swEnabled = meta['enabled'] != false && enabled;
        final onToggle = _childAction(context, children, '.toggle');
        return Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            UiKitSwitch(
              value: meta['value'] == true,
              locked: meta['locked'] == true,
              onChanged: swEnabled && onToggle != null
                  ? (_) => onToggle()
                  : null,
            ),
            if (label.isNotEmpty) ...[
              const SizedBox(width: 10),
              Flexible(
                child: Text(
                  label,
                  style: UiType.textStyle(
                    13,
                    weight: FontWeight.w500,
                    color: c.strong,
                  ),
                ),
              ),
            ],
          ],
        );

      case 'select':
        final label = meta['label'] as String? ?? text;
        final selectedId = meta['selectedId'] as String?;
        final options = (meta['options'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        final btnChildren = children.where((ch) => ch['kind'] == 1).toList();
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (label.isNotEmpty) ...[
              Text(
                label,
                style: UiType.textStyle(
                  12,
                  weight: FontWeight.w600,
                  color: c.muted,
                ),
              ),
              const SizedBox(height: 6),
            ],
            Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                for (var i = 0; i < options.length; i++)
                  UiKitButton(
                    label: '${options[i]['label'] ?? options[i]['id'] ?? ''}',
                    compact: true,
                    style: options[i]['id'] == selectedId
                        ? UiKitButtonStyle.primary
                        : UiKitButtonStyle.secondary,
                    onPressed: i < btnChildren.length
                        ? _dispatchButton(context, btnChildren[i])
                        : null,
                  ),
              ],
            ),
          ],
        );

      case 'progress':
        return UiProgress(
          determinate: meta['indeterminate'] != true,
          value: (meta['value'] as num?)?.toDouble() ?? 0.0,
          label: meta['label'] as String? ?? text,
        );

      case 'infobar':
        final severity = switch (meta['tone'] as String? ?? 'info') {
          'danger' => 'error',
          'warning' => 'warning',
          'success' => 'success',
          _ => 'info',
        };
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            UiInfoBar(
              severity: severity,
              title: meta['title'] as String? ?? text,
              body: meta['body'] as String? ?? '',
            ),
            if (widgets.isNotEmpty) ...[
              const SizedBox(height: 8),
              Wrap(spacing: 8, runSpacing: 6, children: widgets),
            ],
          ],
        );

      case 'datatable':
        final title = meta['title'] as String? ?? text;
        final colLabels = (meta['columns'] as List? ?? const [])
            .map((e) => '$e')
            .toList();
        final rows = (meta['rows'] as List? ?? const [])
            .whereType<List>()
            .map((r) => r.map((cell) => '$cell').toList())
            .toList();
        final columns = [
          for (var cIdx = 0; cIdx < colLabels.length; cIdx++)
            TableColumn(
              colLabels[cIdx],
              numeric:
                  rows.isNotEmpty &&
                  rows.every(
                    (r) =>
                        cIdx < r.length &&
                        double.tryParse(
                              r[cIdx].replaceAll(RegExp(r'[^0-9.\-]'), ''),
                            ) !=
                            null &&
                        RegExp(r'\d').hasMatch(r[cIdx]),
                  ),
            ),
        ];
        final sourceUri = meta['sourceNeuronUri'] as String?;
        final copyAction = _childAction(context, children, '.copy');
        final sortAction = _childAction(context, children, '.sort');
        final filterAction = _childAction(context, children, '.filter');
        return TableCard(
          source: _cardSource(context, sourceUri ?? 'Data Table'),
          data: TableData(
            title: title,
            columns: columns,
            rows: rows,
            from: sourceUri ?? 'DigitalBrain',
            sourceNeuronUri: sourceUri,
          ),
          state: rows.isEmpty ? TableState.empty : TableState.ready,
          onCopyNeuronUri: copyAction != null ? (_) => copyAction() : null,
          onSort: sortAction != null ? (_, _) => sortAction() : null,
          onFilter: filterAction != null ? (_) => filterAction() : null,
        );

      case 'chart':
        final chartKind =
            (meta['kind'] as String? ?? 'bar').toLowerCase() == 'line'
            ? ChartKind.line
            : ChartKind.columns;
        final subtitle = meta['subtitle'] as String?;
        final sourceUri = meta['sourceNeuronUri'] as String?;
        final points = (meta['points'] as List? ?? const [])
            .whereType<Map>()
            .map(
              (p) => ChartPoint(
                '${p['label'] ?? p['id'] ?? ''}',
                (p['value'] as num?)?.toDouble() ?? 0,
              ),
            )
            .toList();
        final comparePoints = (meta['comparePoints'] as List?)
            ?.whereType<Map>()
            .map((p) => (p['value'] as num?)?.toDouble() ?? 0.0)
            .toList();
        final copyAction = _childAction(context, children, '.copy');
        final kindAction = _childAction(context, children, '.kind');
        return ChartCard(
          source: _cardSource(context, subtitle ?? sourceUri ?? 'Chart'),
          data: ChartData(
            title: meta['title'] as String? ?? text,
            points: points,
            from: subtitle ?? sourceUri ?? 'DigitalBrain',
            prefix: meta['unit'] as String? ?? '',
            compare: comparePoints,
            seriesName: meta['seriesName'] as String? ?? 'Primary',
            compareName: meta['compareName'] as String? ?? '',
            sourceNeuronUri: sourceUri,
          ),
          state: points.isEmpty ? ChartState.empty : ChartState.ready,
          kind: chartKind,
          onCopyNeuronUri: copyAction != null ? (_) => copyAction() : null,
          onSwitchChartKind: kindAction != null ? (_) => kindAction() : null,
        );

      case 'approval':
        final verb = meta['verb'] as String? ?? 'Execute';
        final target = meta['target'] as String? ?? text;
        final sourceApp = meta['sourceApp'] as String? ?? 'Ino';
        final sourceUri = meta['sourceNeuronUri'] as String?;
        final scopeUri = meta['scopeUri'] as String?;
        final rows = [
          for (final r in (meta['rows'] as List? ?? const []).whereType<Map>())
            ApprovalRow(
              label: '${r['key'] ?? ''}',
              oldValue: 'current',
              newValue: '${r['value'] ?? ''}',
            ),
          if (sourceUri != null && sourceUri.isNotEmpty)
            ApprovalRow(
              label: 'source',
              oldValue: 'neuron',
              newValue: sourceUri,
            ),
          if (scopeUri != null && scopeUri.isNotEmpty)
            ApprovalRow(label: 'scope', oldValue: 'policy', newValue: scopeUri),
        ];
        final approvalState = switch ((meta['state'] as String? ?? 'pending')
            .toLowerCase()) {
          'approved' || 'settled' || 'done' => ApprovalState.approved,
          'rejected' || 'denied' => ApprovalState.rejected,
          'saving' => ApprovalState.saving,
          'error' => ApprovalState.error,
          _ => ApprovalState.pending,
        };
        return ApprovalCard(
          source: _cardSource(context, sourceApp),
          data: ApprovalData(
            title: '$verb $target'.trim(),
            summary:
                (meta['rememberHint'] as String?) ??
                (scopeUri ?? 'Requires explicit confirmation'),
            rows: rows.isEmpty
                ? [
                    ApprovalRow(
                      label: target,
                      oldValue: 'pending',
                      newValue: verb,
                    ),
                  ]
                : rows,
            valueLabel: 'Value',
            systemName: sourceApp,
          ),
          state: approvalState,
          onApprove: _childAction(context, children, '.approve'),
          onReject: _childAction(context, children, '.reject'),
          onEdited: _childAction(context, children, '.edit'),
        );

      case 'choice':
        final optButtons = children.where((ch) => ch['kind'] == 1).toList();
        return ChoiceCard(
          source: _cardSource(context, meta['sourceApp'] as String? ?? 'Ino'),
          question: meta['question'] as String? ?? text,
          options: [
            for (final o
                in (meta['options'] as List? ?? const []).whereType<Map>())
              ChoiceOption('${o['label'] ?? ''}', detail: o['note'] as String?),
          ],
          picked: (meta['selectedIndex'] as num?)?.toInt(),
          onPick: (idx) {
            if (idx >= 0 && idx < optButtons.length) {
              _dispatchButton(context, optButtons[idx])?.call();
            }
          },
        );

      case 'draft':
        final channel = switch ((meta['channel'] as String? ?? 'email')
            .toLowerCase()) {
          'slack' => DraftChannel.slack,
          'sms' || 'message' || 'post' => DraftChannel.sms,
          _ => DraftChannel.email,
        };
        return DraftCard(
          source: _cardSource(context, channel.noun),
          data: DraftData(
            channel: channel,
            to: meta['recipient'] as String? ?? '',
            subject: meta['subject'] as String?,
            body: meta['body'] as String? ?? '',
            from: 'owner@intochat.os',
          ),
          state: meta['sent'] == true ? DraftState.sent : DraftState.pending,
          onSend: _childAction(context, children, '.send'),
          onDiscard: _childAction(context, children, '.cancel'),
        );

      case 'record':
        final sourceApp = meta['sourceApp'] as String? ?? 'DigitalBrain';
        return RecordCard(
          source: _cardSource(context, sourceApp),
          data: RecordData(
            objectLabel: meta['kind'] as String? ?? 'Record',
            name: meta['title'] as String? ?? text,
            subtitle: meta['subtitle'] as String?,
            fields: [
              for (final f
                  in (meta['fields'] as List? ?? const []).whereType<Map>())
                RecordField('${f['key'] ?? ''}', '${f['value'] ?? ''}'),
            ],
            linkLabel: 'Open in $sourceApp',
          ),
          onOpen: _childAction(context, children, '.open'),
        );

      case 'secret':
        final service = meta['service'] as String? ?? text;
        final keyName = meta['keyName'] as String? ?? 'API_KEY';
        return SecretCard(
          source: _cardSource(context, service),
          data: SecretRequestData(
            name: '$service ($keyName)',
            why:
                meta['hint'] as String? ??
                'Stored encrypted in your personal vault.',
          ),
          state: meta['saved'] == true
              ? SecretState.saved
              : SecretState.pending,
          onEnter: _childAction(context, children, '.save'),
          onSkip: _childAction(context, children, '.skip'),
        );

      case 'job':
        final currentStep = (meta['currentStep'] as num?)?.toInt() ?? 0;
        final done = meta['done'] == true;
        final rawSteps = (meta['steps'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        return JobCard(
          source: _cardSource(context, 'Background Job'),
          data: JobData(
            title: meta['title'] as String? ?? text,
            steps: [
              for (var i = 0; i < rawSteps.length; i++)
                JobStep(
                  '${rawSteps[i]['label'] ?? ''}',
                  detail: rawSteps[i]['detail'] as String?,
                  status: done || i < currentStep
                      ? StepStatus.done
                      : (i == currentStep
                            ? StepStatus.running
                            : StepStatus.waiting),
                ),
            ],
            progress: (meta['progress'] as num?)?.toDouble() ?? 0.5,
            elapsed: meta['eta'] as String?,
          ),
          state: done ? JobState.done : JobState.running,
          onCancel: _childAction(context, children, '.cancel'),
        );

      case 'brain3d':
        final rawNodes = (meta['nodes'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        final nodes3d = <BrainNode3D>[
          for (var i = 0; i < rawNodes.length; i++)
            () {
              final n = rawNodes[i];
              final hasExplicitCoords =
                  n['x'] is num || n['y'] is num || n['z'] is num;
              (double, double, double) pos;
              if (hasExplicitCoords) {
                final rx = (n['x'] as num?)?.toDouble() ?? 0.0;
                final ry = (n['y'] as num?)?.toDouble() ?? 0.0;
                final rz = (n['z'] as num?)?.toDouble() ?? 0.0;
                final scale =
                    (rx.abs() > 1.5 || ry.abs() > 1.5 || rz.abs() > 1.5)
                    ? 120.0
                    : 1.0;
                pos = (rx / scale, ry / scale, rz / scale);
              } else {
                final phase =
                    (n['orbitPhase'] as num?)?.toDouble() ??
                    (i * 2 * pi / max(1, rawNodes.length));
                final tilt = (n['orbitTilt'] as num?)?.toDouble() ?? 0.2;
                final orbitR =
                    ((n['orbitRadius'] as num?)?.toDouble() ?? 140.0) / 260.0;
                pos = i == 0 && orbitR == 0
                    ? (0.0, 0.0, 0.0)
                    : (
                        orbitR * cos(phase),
                        orbitR * sin(tilt) * sin(phase),
                        orbitR * sin(phase),
                      );
              }
              final nodeId = '${n['id'] ?? ''}';
              final sigs =
                  (n['signalsPerMin'] as num?)?.toInt() ??
                  switch (nodeId) {
                    'creator' => 48,
                    'aspire' => 96,
                    'memory' => 32,
                    'bdd' => 24,
                    _ => 18 + i * 12,
                  };
              return BrainNode3D(
                id: nodeId,
                uri:
                    '${n['uri'] ?? n['subtitle'] ?? 'neuron://owner/${n['category'] ?? 'os'}/$nodeId'}',
                label: '${n['label'] ?? ''}',
                kind: BrainNodeKind.app,
                moduleId: '${n['category'] ?? 'DigitalBrain'}',
                subtitle: n['subtitle'] as String?,
                signalsPerMin: sigs,
                appId: nodeId.startsWith('app.') ? nodeId.substring(4) : nodeId,
                position: pos,
                status: switch ('${n['status'] ?? 'healthy'}'.toLowerCase()) {
                  'pulse' || 'working' => BrainNodeStatus.working,
                  'attention' ||
                  'warn' ||
                  'warning' => BrainNodeStatus.needsYou,
                  'error' || 'danger' => BrainNodeStatus.error,
                  'done' || 'ok' => BrainNodeStatus.active,
                  _ => BrainNodeStatus.idle,
                },
              );
            }(),
        ];
        final synapses3d = [
          for (final s
              in (meta['synapses'] as List? ?? const []).whereType<Map>())
            BrainSynapse3D(
              fromId: '${s['fromId'] ?? ''}',
              toId: '${s['toId'] ?? ''}',
              signalLabel: s['signalType'] as String?,
              active: s['active'] != false,
              trailStep: (s['trailStep'] as num?)?.toInt(),
            ),
        ];
        final nodeButtons = children.where((ch) => ch['kind'] == 1).toList();
        return _InteractiveBrainSceneBox(
          height: (meta['height'] as num?)?.toDouble() ?? 340.0,
          nodes: nodes3d.isEmpty ? defaultBrainNodes : nodes3d,
          synapses: synapses3d.isEmpty ? defaultBrainSynapses : synapses3d,
          initialSelectedId: meta['selectedId'] as String?,
          pauseRotation: meta['pauseRotation'] == true,
          zoomTargetId: meta['zoomTargetId'] as String?,
          targetZoom: (meta['targetZoom'] as num?)?.toDouble() ?? 1.65,
          interactive: meta['interactive'] != false,
          showHud: meta['showHud'] != false,
          onSelectNodeIndex: (idx) {
            if (idx >= 0 && idx < nodeButtons.length) {
              _dispatchButton(context, nodeButtons[idx])?.call();
            }
          },
        );

      case 'webbrowser':
        final url =
            '${meta['url'] ?? meta['uri'] ?? meta['src'] ?? 'http://localhost:18082'}';
        final studioTitle = (meta['title'] as String?)?.isNotEmpty == true
            ? meta['title'] as String
            : (text.isNotEmpty
                  ? text
                  : (url.contains('18083')
                        ? 'FilmCraft WASM Studio'
                        : 'PhotoCraft WASM Studio'));
        final executionMode =
            '${meta['executionMode'] ?? meta['mode'] ?? 'wasm-ui'}';
        final height = (meta['height'] as num?)?.toDouble() ?? 360.0;
        final reloadAction =
            _childAction(context, children, '.reload') ??
            _childAction(context, children, '.open');
        return _InteractiveWasmBrowserFrame(
          title: studioTitle,
          url: url,
          executionMode: executionMode,
          height: height,
          subtitle: meta['subtitle'] as String? ?? meta['status'] as String?,
          onReload: reloadAction,
          children: widgets,
        );

      case 'neuronRef':
        final copyAction = _childAction(context, children, '.copy');
        final embeddedRefs = [
          for (var i = 0; i < children.length; i++)
            if (children[i]['kind'] == 5) widgets[i],
        ];
        return UiKitSurface(
          padding: const EdgeInsets.all(12),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  ViewBadge(
                    text: meta['kind'] as String? ?? 'neuron',
                    tone: 2,
                    icon: 'cpu',
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      (meta['title'] as String?) ??
                          (meta['neuronUri'] as String? ?? text),
                      style: UiType.tabularStyle(12, color: c.strong),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  if (copyAction != null)
                    UiKitButton(
                      label: 'Copy URI',
                      icon: 'copy',
                      compact: true,
                      style: UiKitButtonStyle.ghost,
                      onPressed: copyAction,
                    ),
                ],
              ),
              if (embeddedRefs.isNotEmpty) ...[
                const SizedBox(height: 10),
                ...embeddedRefs,
              ],
            ],
          ),
        );

      default:
        break;
    }

    return SizedBox(
      width: double.infinity,
      child: UiKitSurface(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (text.isNotEmpty)
              Text(text, style: Theme.of(context).textTheme.titleLarge),
            ...widgets,
          ],
        ),
      ),
    );
  }

  /// [slotted]: the node fills an explicit Row/Column extent.
  Widget _node(
    BuildContext context,
    Map node,
    int depth, {
    bool slotted = false,
  }) {
    if (depth > 32) return const Text('This component cannot be displayed.');
    final key = ValueKey('view:$name:${node['key']}');
    final children = (node['children'] as List? ?? const [])
        .whereType<Map>()
        .take(512)
        .toList();
    // Built on demand so a List builds only its visible rows.
    final extents = node['kind'] == 2 || node['kind'] == 3
        ? node['extents'] as List? ?? const []
        : const [];
    late final widgets = [
      for (var i = 0; i < children.length; i++)
        _node(
          context,
          children[i],
          depth + 1,
          slotted: switch (extents.elementAtOrNull(i)) {
            final num extent => extent > 0,
            _ => false,
          },
        ),
    ];
    final text = node['text'] as String? ?? '';
    Widget content;
    switch (node['kind']) {
      case 0:
        final meta = _tryParseUiMeta(node['detail']);
        if (meta != null) {
          final c = UiColors.of(context);
          if ((meta['_ui'] as String? ?? '') == 'heading') {
            final fontSize = switch ((meta['level'] as num?)?.toInt() ?? 2) {
              1 => 22.0,
              2 => 17.0,
              _ => 15.0,
            };
            final subtitle = meta['subtitle'] as String?;
            content = Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  text,
                  style: UiType.displayStyle(
                    fontSize,
                    weight: FontWeight.w700,
                    color: c.strong,
                  ),
                ),
                if (subtitle != null && subtitle.isNotEmpty) ...[
                  const SizedBox(height: 4),
                  Text(subtitle, style: UiType.textStyle(12.5, color: c.muted)),
                ],
              ],
            );
          } else {
            final size = (meta['size'] as num?)?.toDouble() ?? 13.0;
            final color = switch (meta['tone'] as String? ?? 'default') {
              'strong' => c.strong,
              'muted' => c.muted,
              'subtle' => c.subtle,
              'accent' => c.accent,
              'success' => c.done,
              'warning' => c.working,
              'danger' => c.error,
              _ => c.body,
            };
            final style = meta['mono'] == true
                ? UiType.tabularStyle(size, color: color)
                : UiType.textStyle(
                    size,
                    color: color,
                    tnum: meta['tabular'] == true,
                  );
            content = SelectionArea(child: Text(text, style: style));
          }
        } else {
          content = markdown(text);
        }
      case 1:
        final forceCompact = node['detail'] == 'small';
        content = LayoutBuilder(
          builder: (context, constraints) => viewButton(
            node,
            _dispatchButton(context, node),
            compact: forceCompact || constraints.maxHeight < 48,
          ),
        );
        if (!slotted) {
          content = Align(
            alignment: AlignmentDirectional.centerStart,
            heightFactor: 1,
            child: content,
          );
        }
      case 2:
      case 3:
        if (node['kind'] == 2 &&
            node['key'] == 'settings' &&
            children.length >= 2 &&
            children[0]['key'] == 'tabs') {
          content = _buildSettingsDesktopView(
            context,
            node,
            children,
            widgets,
            depth,
          );
          break;
        }
        if (const {
          'files-full',
          'files-main-wrap',
          'files-main',
        }.contains(node['key'])) {
          content = _buildFilesFinderDesktopView(
            context,
            node,
            children,
            depth,
          );
          break;
        }
        if (node['kind'] == 3 && node['key'] == 'breadcrumbs') {
          content = _buildWinUiBreadcrumbBar(context, children, widgets);
          break;
        }
        if (node['kind'] == 3 &&
            const {'toolbar', 'navigation'}.contains(node['key'])) {
          content = Wrap(
            spacing: 6,
            runSpacing: 6,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: widgets,
          );
          break;
        }
        content = layout(
          {
            'mode': node['kind'] == 3
                ? 'row'
                : (node['extents'] as List? ?? const []).isEmpty
                ? 'list'
                : 'column',
            'extents': node['extents'],
            'gap': node['gap'],
            'followEnd': node['followEnd'],
            'stretch': const {
              'view',
              'split',
              'files-full',
              'browse-split',
            }.contains(node['key']),
          },
          widgets,
          children,
        );
      case 4:
        final meta = _tryParseUiMeta(node['detail']);
        if (meta != null) {
          content = _buildRichCard(context, text, meta, children, widgets);
        } else {
          content = SizedBox(
            width: double.infinity,
            child: UiKitSurface(
              child: SingleChildScrollView(
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    if (text.isNotEmpty)
                      Text(text, style: Theme.of(context).textTheme.titleLarge),
                    if (node['detail'] case final String detail
                        when detail.isNotEmpty)
                      markdown(detail),
                    ...widgets,
                  ],
                ),
              ),
            ),
          );
        }
      case 5:
        final target = node['reference'];
        if (target is Map &&
            target['kind'] is String &&
            target['name'] is String) {
          return reference(target, key);
        }
        content = const Text('This component cannot be displayed.');
      case 6:
        content = ViewMenu(
          node: node,
          action: (node) => _dispatchButton(context, node),
          reference: (child) => switch (child['reference']) {
            final Map target
                when target['kind'] is String && target['name'] is String =>
              reference(target, ValueKey('view:$name:${child['key']}')),
            _ => const SizedBox.shrink(),
          },
        );
      case 7:
        content = ViewModal(
          node: node,
          children: [
            for (final child in children)
              (
                child,
                child['kind'] == 1
                    ? _node(context, child, depth + 1, slotted: true)
                    : widgets[children.indexOf(child)],
              ),
          ],
          action: (node) => _dispatchButton(context, node),
        );
      case 8:
        content = ViewToast(
          node: node,
          generation: generation,
          action: (node) => _dispatchButton(context, node),
        );
      case 9:
        content = ViewList(
          count: children.length,
          row: (i) => _node(context, children[i], depth + 1),
        );
      case 10:
        content = ViewBadge(
          text: text,
          tone: node['tone'],
          icon: node['icon'] is String ? node['icon'] as String : null,
        );
      case 11:
        content = ViewAvatar(initials: text);
      case 12:
        content = ViewDivider(label: text);
      case 13:
        content = ViewTabs(
          node: node,
          action: (node) => _dispatchButton(context, node),
        );
      default:
        content = const Text('This component cannot be displayed.');
    }
    return KeyedSubtree(key: key, child: content);
  }

  Widget _buildWinUiBreadcrumbBar(
    BuildContext context,
    List<Map> children,
    List<Widget> widgets,
  ) {
    final items = <IcBreadcrumbItem>[];
    final trailingActions = <Widget>[];
    String? neuronUri;
    Key? neuronUriKey;

    for (var i = 0; i < children.length; i++) {
      final child = children[i];
      final childKey = child['key'] as String? ?? '';
      final fullKey = ValueKey('view:$name:$childKey');
      if (childKey == 'crumb-neuron-uri' || child['kind'] == 10) {
        neuronUri = child['text'] as String?;
        neuronUriKey = fullKey;
      } else if (childKey.startsWith('crumb-copy-')) {
        trailingActions.add(widgets[i]);
      } else if (child['kind'] == 1) {
        final iconSlug = child['icon'] as String?;
        items.add(
          IcBreadcrumbItem(
            id: childKey,
            label: child['text'] as String? ?? childKey,
            icon: iconSlug != null
                ? uiIcon(iconSlug)
                : (childKey == 'crumb-root'
                      ? LucideIcons.hardDrive
                      : LucideIcons.folder),
            onTap: _dispatchButton(context, child),
            itemKey: fullKey,
          ),
        );
      } else {
        trailingActions.add(widgets[i]);
      }
    }

    return IcBreadcrumbBar(
      items: items,
      neuronUri: neuronUri,
      neuronUriKey: neuronUriKey,
      trailingActions: trailingActions,
    );
  }

  Widget _buildFilesFinderDesktopView(
    BuildContext context,
    Map node,
    List<Map> children,
    int depth,
  ) {
    Map? sideNode;
    Map? mainNode;
    Map? extrasNode;
    Map? emptyNode;

    final rootKey = node['key'] as String? ?? '';
    if (rootKey == 'files-full') {
      for (final c in children) {
        final k = c['key'] as String? ?? '';
        if (k == 'files-side') {
          sideNode = c;
        } else if (k == 'files-main-wrap') {
          for (final mc
              in (c['children'] as List? ?? const []).whereType<Map>()) {
            final mk = mc['key'] as String? ?? '';
            if (mk == 'files-main') mainNode = mc;
            if (mk == 'files-empty') emptyNode = mc;
            if (mk == 'files-extras') extrasNode = mc;
          }
        } else if (k == 'files-main') {
          mainNode = c;
        } else if (k == 'files-empty') {
          emptyNode = c;
        }
      }
    } else if (rootKey == 'files-main-wrap') {
      for (final c in children) {
        final k = c['key'] as String? ?? '';
        if (k == 'files-main') mainNode = c;
        if (k == 'files-empty') emptyNode = c;
        if (k == 'files-extras') extrasNode = c;
      }
    } else if (rootKey == 'files-main') {
      mainNode = node;
    }

    // 1. Parse storages from files-side
    final storages = <IcFilesStorageEntry>[];
    VoidCallback? onOpenSettings;
    if (sideNode != null) {
      for (final sc
          in (sideNode['children'] as List? ?? const []).whereType<Map>()) {
        final sk = sc['key'] as String? ?? '';
        if (sk == 'side-open-settings' || sk == 'open-settings') {
          onOpenSettings = _dispatchButton(context, sc);
        } else if (sk.startsWith('storage-')) {
          final cardChildren = (sc['children'] as List? ?? const [])
              .whereType<Map>()
              .toList();
          final openBtn = cardChildren.isNotEmpty ? cardChildren.first : null;
          final btnIcon = openBtn?['icon'] as String? ?? 'hard-drive';
          final btnText = openBtn?['text'] as String? ?? 'Browse';
          final selected = openBtn?['style'] == 1;
          storages.add(
            IcFilesStorageEntry(
              key: sk,
              label: sc['text'] as String? ?? sk,
              quotaText: sc['detail'] as String? ?? 'Local folder',
              icon: uiIcon(btnIcon) ?? LucideIcons.hardDrive,
              selected: selected,
              unavailable: btnText == 'Try again',
              onTap: openBtn != null ? _dispatchButton(context, openBtn) : null,
            ),
          );
        }
      }
    }

    // 2. Parse empty state (if no storages connected)
    if (emptyNode != null) {
      for (final ec
          in (emptyNode['children'] as List? ?? const []).whereType<Map>()) {
        if (ec['key'] == 'open-settings') {
          onOpenSettings = _dispatchButton(context, ec);
        }
      }
    }

    final VoidCallback? onConnectGoogleDrive = onActivate != null
        ? () => onActivate!({
            'kind': 'connections',
            'connector': 'google-drive',
            'connection': 'google-drive',
          })
        : onOpenSettings;

    // 3. Parse files-main children
    final breadcrumbs = <IcBreadcrumbItem>[];
    final trailingBreadcrumbWidgets = <Widget>[];
    String? activeNeuronUri;

    VoidCallback? onNavigateUp;
    VoidCallback? onSearchAll;
    VoidCallback? onApplyFilter;

    VoidCallback? onUpload;
    VoidCallback? onNewFolder;
    VoidCallback? onRename;
    VoidCallback? onMove;
    VoidCallback? onCopy;
    VoidCallback? onAskIno;
    VoidCallback? onDownload;
    VoidCallback? onDelete;

    Widget? filterFieldWidget;
    Widget? searchFieldWidget;

    var activeSort = 'name';
    VoidCallback? onSortName;
    VoidCallback? onSortSize;
    VoidCallback? onSortModified;

    final items = <IcFilesItemEntry>[];
    var loading = false;
    String? statusTitle;
    String? statusBody;
    VoidCallback? onRetry;
    String? emptyBodyText;
    VoidCallback? onClearFilter;
    VoidCallback? onBackToFolder;
    VoidCallback? onLoadMore;
    String? pageInfoText;
    VoidCallback? onPrevPage;
    VoidCallback? onNextPage;

    String? selectedName;
    String? selectedNeuronUri;
    String? selectedMeta;
    String? inoHandoffPrompt;
    String? previewText;
    String? previewNote;
    Widget? previewImageWidget;
    Widget? previewAppCard;

    String activeWhereLabel() {
      final folderCrumbs = breadcrumbs
          .where((b) => b.id != 'crumb-root')
          .toList();
      if (folderCrumbs.isNotEmpty) {
        return folderCrumbs.map((b) => b.label).join('/');
      }
      final selectedStorage = storages.where((s) => s.selected).firstOrNull;
      return selectedStorage?.label ?? 'Storages';
    }

    void parseListColumn(Map listCol) {
      for (final lc
          in (listCol['children'] as List? ?? const []).whereType<Map>()) {
        final lk = lc['key'] as String? ?? '';
        if (lk == 'columns') {
          for (final colBtn
              in (lc['children'] as List? ?? const []).whereType<Map>()) {
            final ck = colBtn['key'] as String? ?? '';
            if (ck == 'sort-name') {
              onSortName = _dispatchButton(context, colBtn);
              if (colBtn['style'] == 1) activeSort = 'name';
            } else if (ck == 'sort-size') {
              onSortSize = _dispatchButton(context, colBtn);
              if (colBtn['style'] == 1) activeSort = 'size';
            } else if (ck == 'sort-modified') {
              onSortModified = _dispatchButton(context, colBtn);
              if (colBtn['style'] == 1) activeSort = 'modified';
            }
          }
        } else if (lk == 'loading') {
          loading = true;
        } else if (lk == 'status-title') {
          statusTitle = lc['text'] as String?;
        } else if (lk == 'status-body') {
          statusBody = lc['text'] as String?;
        } else if (lk == 'try-again') {
          onRetry = _dispatchButton(context, lc);
        } else if (lk == 'filter-body' || lk == 'empty-body') {
          emptyBodyText = lc['text'] as String?;
        } else if (lk == 'clear-filter') {
          onClearFilter = _dispatchButton(context, lc);
        } else if (lk == 'back-folder') {
          onBackToFolder = _dispatchButton(context, lc);
        } else if (lk == 'more') {
          onLoadMore = _dispatchButton(context, lc);
        } else if (lk == 'items') {
          String? currentGroup;
          final fallbackWhere = activeWhereLabel();
          for (final row
              in (lc['children'] as List? ?? const []).whereType<Map>()) {
            final rk = row['key'] as String? ?? '';
            if (rk.startsWith('group-div-')) {
              currentGroup = row['text'] as String?;
            } else if (rk.startsWith('item-row-') ||
                rk.startsWith('search-row-')) {
              final rowChildren = (row['children'] as List? ?? const [])
                  .whereType<Map>()
                  .toList();
              if (rowChildren.isEmpty) continue;
              final openBtn = rowChildren[0];
              final metaBadge = rowChildren.length > 1 ? rowChildren[1] : null;
              final selectBtn = rowChildren.length > 2 ? rowChildren[2] : null;

              final nameText = openBtn['text'] as String? ?? rk;
              final iconSlug = openBtn['icon'] as String? ?? 'file';
              final metaRaw = metaBadge?['text'] as String? ?? '';
              final parts = metaRaw.split(' · ');

              String kindLabel;
              String sizeLabel;
              String modifiedLabel;
              String? itemWhereLabel;
              if (parts.length >= 4) {
                kindLabel = parts[0];
                sizeLabel = parts[1];
                modifiedLabel = parts[2];
                itemWhereLabel = parts.sublist(3).join(' · ');
              } else if (parts.length == 3) {
                kindLabel = parts[0];
                sizeLabel = parts[1];
                modifiedLabel = parts[2];
              } else if (parts.length == 2) {
                kindLabel = parts[0];
                sizeLabel = parts[1];
                modifiedLabel = '—';
              } else {
                final isFolderGuess =
                    iconSlug == 'folder' || metaRaw == 'Folder';
                kindLabel = isFolderGuess ? 'Folder' : 'File';
                sizeLabel = isFolderGuess
                    ? '--'
                    : (metaRaw.isEmpty ? '--' : metaRaw);
                modifiedLabel = '—';
              }

              final isFolder = iconSlug == 'folder' || kindLabel == 'Folder';
              final isSelected =
                  openBtn['style'] == 1 || selectBtn?['style'] == 1;
              final openCallback = _dispatchButton(context, openBtn);
              final selectCallback = selectBtn != null
                  ? _dispatchButton(context, selectBtn)
                  : openCallback;

              items.add(
                IcFilesItemEntry(
                  key: rk,
                  name: nameText,
                  isFolder: isFolder,
                  selected: isSelected,
                  kindLabel: kindLabel,
                  sizeLabel: sizeLabel,
                  modifiedLabel: modifiedLabel,
                  whereLabel: itemWhereLabel ?? currentGroup ?? fallbackWhere,
                  groupLabel: currentGroup,
                  onOpen: openCallback,
                  onSelect: selectCallback,
                ),
              );
            } else if (rk == 'items-pager') {
              for (final pc
                  in (row['children'] as List? ?? const []).whereType<Map>()) {
                final pk = pc['key'] as String? ?? '';
                if (pk == 'page-prev' && pc['enabled'] != false) {
                  onPrevPage = _dispatchButton(context, pc);
                } else if (pk == 'page-info') {
                  pageInfoText = pc['text'] as String?;
                } else if (pk == 'page-next' && pc['enabled'] != false) {
                  onNextPage = _dispatchButton(context, pc);
                }
              }
            }
          }
        }
      }
    }

    void parseQuickViewCard(Map qv) {
      selectedName = qv['text'] as String?;
      final detail = qv['detail'] as String?;
      if (detail != null && detail.startsWith('neuron://')) {
        selectedNeuronUri = detail;
      }
      for (final qc in (qv['children'] as List? ?? const []).whereType<Map>()) {
        final qk = qc['key'] as String? ?? '';
        if (qk == 'quick-view-meta') {
          selectedMeta = qc['text'] as String?;
        } else if (qk == 'quick-view-uri') {
          selectedNeuronUri = qc['text'] as String?;
        } else if (qk == 'ino-handoff-prompt') {
          inoHandoffPrompt = qc['text'] as String?;
        } else if (qk == 'quick-view-actions') {
          for (final act
              in (qc['children'] as List? ?? const []).whereType<Map>()) {
            if (act['key'] == 'ask-ino' && act['enabled'] != false) {
              onAskIno = _dispatchButton(context, act);
            }
          }
        } else if (qk == 'preview-app') {
          previewAppCard = _node(context, qc, depth + 2);
        } else if (qk == 'preview') {
          for (final pc
              in (qc['children'] as List? ?? const []).whereType<Map>()) {
            final pk = pc['key'] as String? ?? '';
            if (pk == 'preview-text') {
              previewText = pc['text'] as String?;
            } else if (pk == 'preview-note') {
              previewNote = pc['text'] as String?;
            } else if (pk == 'preview-image') {
              previewImageWidget = _node(context, pc, depth + 3);
            }
          }
        }
      }
    }

    if (mainNode != null) {
      for (final mc
          in (mainNode['children'] as List? ?? const []).whereType<Map>()) {
        final mk = mc['key'] as String? ?? '';
        if (mk == 'navigation') {
          for (final navBtn
              in (mc['children'] as List? ?? const []).whereType<Map>()) {
            final nk = navBtn['key'] as String? ?? '';
            if (nk == 'up') {
              if (navBtn['enabled'] != false) {
                onNavigateUp = _dispatchButton(context, navBtn);
              }
            } else if (nk == 'search-all') {
              onSearchAll = _dispatchButton(context, navBtn);
            } else if (nk == 'apply-filter') {
              onApplyFilter = _dispatchButton(context, navBtn);
            }
          }
        } else if (mk == 'breadcrumbs') {
          for (final bc
              in (mc['children'] as List? ?? const []).whereType<Map>()) {
            final bk = bc['key'] as String? ?? '';
            final fullKey = ValueKey('view:$name:$bk');
            if (bk == 'crumb-neuron-uri' || bc['kind'] == 10) {
              activeNeuronUri = bc['text'] as String?;
            } else if (bk.startsWith('crumb-copy-')) {
              trailingBreadcrumbWidgets.add(_node(context, bc, depth + 2));
            } else if (bc['kind'] == 1) {
              final iconSlug = bc['icon'] as String?;
              breadcrumbs.add(
                IcBreadcrumbItem(
                  id: bk,
                  label: bc['text'] as String? ?? bk,
                  icon: iconSlug != null
                      ? uiIcon(iconSlug)
                      : (bk == 'crumb-root'
                            ? LucideIcons.hardDrive
                            : LucideIcons.folder),
                  onTap: _dispatchButton(context, bc),
                  itemKey: fullKey,
                ),
              );
            }
          }
        } else if (mk == 'toolbar') {
          for (final tb
              in (mc['children'] as List? ?? const []).whereType<Map>()) {
            final tk = tb['key'] as String? ?? '';
            final enabled = tb['enabled'] != false;
            switch (tk) {
              case 'upload':
                if (enabled) onUpload = _dispatchButton(context, tb);
              case 'mkdir':
                if (enabled) onNewFolder = _dispatchButton(context, tb);
              case 'rename':
                if (enabled) onRename = _dispatchButton(context, tb);
              case 'move':
                if (enabled) onMove = _dispatchButton(context, tb);
              case 'copy':
                if (enabled) onCopy = _dispatchButton(context, tb);
              case 'ask-ino-toolbar':
                if (enabled) onAskIno ??= _dispatchButton(context, tb);
              case 'download':
                if (enabled) onDownload = _dispatchButton(context, tb);
              case 'delete':
                if (enabled) onDelete = _dispatchButton(context, tb);
            }
          }
        } else if (mk == 'filter') {
          filterFieldWidget = _node(context, mc, depth + 1);
        } else if (mk == 'search') {
          searchFieldWidget = _node(context, mc, depth + 1);
        } else if (mk == 'files-list') {
          parseListColumn(mc);
        } else if (mk == 'quick-view') {
          parseQuickViewCard(mc);
        } else if (mk == 'browse-split') {
          for (final sc
              in (mc['children'] as List? ?? const []).whereType<Map>()) {
            final sk = sc['key'] as String? ?? '';
            if (sk == 'files-list') parseListColumn(sc);
            if (sk == 'quick-view') parseQuickViewCard(sc);
          }
        }
      }
    }

    // 4. Parse files-extras children
    String? noticeText;
    Widget? nameFieldWidget;
    Widget? contentFieldWidget;
    VoidCallback? onWriteFile;
    VoidCallback? onPaste;
    Widget? confirmationsWidget;
    final operationWidgets = <Widget>[];

    if (extrasNode != null) {
      for (final ec
          in (extrasNode['children'] as List? ?? const []).whereType<Map>()) {
        final ek = ec['key'] as String? ?? '';
        if (ek == 'message') {
          noticeText = ec['text'] as String?;
        } else if (ek == 'input-name') {
          nameFieldWidget = _node(context, ec, depth + 1);
        } else if (ek == 'input-content') {
          contentFieldWidget = _node(context, ec, depth + 1);
        } else if (ek == 'write') {
          if (ec['enabled'] != false) {
            onWriteFile = _dispatchButton(context, ec);
          }
        } else if (ek == 'paste') {
          if (ec['enabled'] != false) {
            onPaste = _dispatchButton(context, ec);
          }
        } else if (ek == 'confirmations') {
          confirmationsWidget = _node(context, ec, depth + 1);
        } else if (ek.startsWith('operation-')) {
          operationWidgets.add(_node(context, ec, depth + 1));
        }
      }
    }

    return IcFilesFinderView(
      storages: storages,
      breadcrumbs: breadcrumbs,
      activeNeuronUri: activeNeuronUri,
      activeDisplayPath: activeWhereLabel(),
      trailingBreadcrumbWidgets: trailingBreadcrumbWidgets,
      items: items,
      activeSort: activeSort,
      onSortName: onSortName,
      onSortSize: onSortSize,
      onSortModified: onSortModified,
      onUp: onNavigateUp,
      onBackFolder: onBackToFolder,
      onSearchAll: onSearchAll,
      onApplyFilter: onApplyFilter,
      onUpload: onUpload,
      onNewFolder: onNewFolder,
      onRename: onRename,
      onMove: onMove,
      onCopy: onCopy,
      onDownload: onDownload,
      onDelete: onDelete,
      onPaste: onPaste,
      onWriteFile: onWriteFile,
      onAskIno: onAskIno,
      onOpenSettings: onOpenSettings,
      onConnectGoogleDrive: onConnectGoogleDrive,
      filterFieldWidget: filterFieldWidget,
      searchFieldWidget: searchFieldWidget,
      nameFieldWidget: nameFieldWidget,
      contentFieldWidget: contentFieldWidget,
      confirmationsWidget: confirmationsWidget,
      operationWidgets: operationWidgets,
      isLoading: loading,
      isEmpty: emptyBodyText != null && items.isEmpty,
      noStorageConnected: emptyNode != null,
      statusTitle: statusTitle,
      statusBody: statusBody ?? emptyBodyText,
      onRetry: onRetry,
      onClearFilter: onClearFilter,
      onLoadMore: onLoadMore,
      pageInfoText: pageInfoText,
      onPrevPage: onPrevPage,
      onNextPage: onNextPage,
      noticeText: noticeText,
      selectedName: selectedName,
      selectedNeuronUri: selectedNeuronUri,
      selectedMeta: selectedMeta,
      inoHandoffPrompt: inoHandoffPrompt,
      previewText: previewText,
      previewNote: previewNote,
      previewImageWidget: previewImageWidget,
      previewAppCard: previewAppCard,
    );
  }

  static IconData _settingsIconFor(String tabKey) => switch (tabKey) {
    'tab-general' => LucideIcons.slidersHorizontal,
    'tab-models' => LucideIcons.cpu,
    'tab-keys' => LucideIcons.keyRound,
    'tab-connections' => LucideIcons.plug,
    'tab-modules' => LucideIcons.boxes,
    'tab-apps' => LucideIcons.layoutGrid,
    'tab-variables' => LucideIcons.braces,
    'tab-notifications' => LucideIcons.bell,
    'tab-archived' => LucideIcons.archive,
    'tab-account' => LucideIcons.userCheck,
    'tab-danger' => LucideIcons.triangleAlert,
    'tab-billing' => LucideIcons.creditCard,
    _ => LucideIcons.settings,
  };

  Widget _buildSettingsDesktopView(
    BuildContext context,
    Map node,
    List<Map> children,
    List<Widget> widgets,
    int depth,
  ) {
    return LayoutBuilder(
      builder: (context, constraints) {
        if (constraints.maxWidth < 680) {
          return layout(
            {
              'mode': 'column',
              'extents': node['extents'],
              'gap': node['gap'],
              'followEnd': node['followEnd'],
            },
            widgets,
            children,
          );
        }
        final c = UiColors.of(context);
        final tabItems = (children[0]['children'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        final bodyNode = children[1];
        final bodyKey = bodyNode['key'] as String? ?? '';
        final bodyChildren = (bodyNode['children'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        final bodyWidgets = [
          for (final child in bodyChildren) _node(context, child, depth + 2),
        ];

        final headingText = bodyChildren.isNotEmpty
            ? bodyChildren[0]['text'] as String? ?? ''
            : '';
        final descriptionText = bodyChildren.length > 1
            ? bodyChildren[1]['text'] as String? ?? ''
            : '';
        final restWidgets = bodyWidgets.length > 2
            ? bodyWidgets.sublist(2)
            : bodyWidgets;
        final currentTheme = localPreferences?.read(PreferenceStore.localTheme);

        return Row(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Container(
              width: 232,
              decoration: BoxDecoration(
                color: c.recessed,
                border: Border(right: BorderSide(color: c.hairline)),
              ),
              padding: const EdgeInsets.fromLTRB(12, 16, 12, 16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Padding(
                    padding: const EdgeInsets.fromLTRB(8, 0, 8, 14),
                    child: Row(
                      children: [
                        Container(
                          width: 26,
                          height: 26,
                          decoration: BoxDecoration(
                            color: c.accentTint,
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: Icon(
                            LucideIcons.settings,
                            size: 14,
                            color: c.accent,
                          ),
                        ),
                        const SizedBox(width: 8),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                'System Settings',
                                style: UiType.textStyle(
                                  13,
                                  weight: FontWeight.w700,
                                  color: c.strong,
                                ),
                              ),
                              Text(
                                'neuron://local/apps/settings',
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: UiType.tabularStyle(
                                  10.5,
                                  color: c.muted,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                  Expanded(
                    child: ListView.separated(
                      itemCount: tabItems.length,
                      separatorBuilder: (_, _) => const SizedBox(height: 2),
                      itemBuilder: (context, index) {
                        final tab = tabItems[index];
                        final tabKey = tab['key'] as String? ?? 'tab-$index';
                        final label = tab['text'] as String? ?? '';
                        final selected = tab['selected'] == true;
                        final onTap = _dispatchButton(context, tab);
                        final isDanger = tabKey == 'tab-danger';
                        return KeyedSubtree(
                          key: ValueKey('view:$name:$tabKey'),
                          child: GestureDetector(
                            onTap: onTap,
                            child: MouseRegion(
                              cursor: onTap != null
                                  ? SystemMouseCursors.click
                                  : SystemMouseCursors.basic,
                              child: AnimatedContainer(
                                duration: IcMotion.fast,
                                height: 34,
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 10,
                                ),
                                decoration: BoxDecoration(
                                  color: selected
                                      ? c.accentTint
                                      : Colors.transparent,
                                  borderRadius: BorderRadius.circular(8),
                                ),
                                child: Row(
                                  children: [
                                    Icon(
                                      _settingsIconFor(tabKey),
                                      size: 15,
                                      color: isDanger
                                          ? c.error
                                          : (selected ? c.accent : c.muted),
                                    ),
                                    const SizedBox(width: 10),
                                    Expanded(
                                      child: Text(
                                        label,
                                        maxLines: 1,
                                        overflow: TextOverflow.ellipsis,
                                        style: UiType.textStyle(
                                          13,
                                          weight: selected
                                              ? FontWeight.w600
                                              : FontWeight.w500,
                                          color: onTap == null && !selected
                                              ? c.subtle
                                              : (isDanger
                                                    ? c.error
                                                    : (selected
                                                          ? c.strong
                                                          : c.body)),
                                        ),
                                      ),
                                    ),
                                  ],
                                ),
                              ),
                            ),
                          ),
                        );
                      },
                    ),
                  ),
                ],
              ),
            ),
            Expanded(
              child: ColoredBox(
                color: c.page,
                child: SingleChildScrollView(
                  key: ValueKey('settings-scroll:$bodyKey'),
                  padding: const EdgeInsets.fromLTRB(36, 28, 36, 48),
                  child: Align(
                    alignment: Alignment.topLeft,
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(maxWidth: 820),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          if (headingText.isNotEmpty)
                            Text(
                              headingText,
                              style: UiType.displayStyle(
                                22,
                                weight: FontWeight.w700,
                                color: c.strong,
                              ),
                            ),
                          if (descriptionText.isNotEmpty) ...[
                            const SizedBox(height: 6),
                            Text(
                              descriptionText,
                              style: UiType.textStyle(13, color: c.muted),
                            ),
                          ],
                          const SizedBox(height: 20),
                          if (bodyKey == 'tab-body-general' &&
                              localPreferences != null) ...[
                            Text(
                              'UI IDENTITY & GLASS MATERIALITY',
                              style: UiType.textStyle(
                                11,
                                weight: FontWeight.w700,
                                color: c.muted,
                              ),
                            ),
                            const SizedBox(height: 10),
                            Wrap(
                              spacing: 10,
                              runSpacing: 10,
                              children: [
                                for (final preset in IcStylePreset.values)
                                  _buildPresetCard(
                                    context,
                                    c,
                                    preset,
                                    currentTheme == preset.id ||
                                        (currentTheme == 'intochat' &&
                                            preset ==
                                                IcStylePreset.fluentAcrylic),
                                  ),
                              ],
                            ),
                            const SizedBox(height: 22),
                          ],
                          UiKitSurface(
                            padding: const EdgeInsets.all(20),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              spacing: 14,
                              children: restWidgets,
                            ),
                          ),
                          if (widgets.length > 2) ...[
                            const SizedBox(height: 14),
                            for (var i = 2; i < widgets.length; i++) widgets[i],
                          ],
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ],
        );
      },
    );
  }

  Widget _buildPresetCard(
    BuildContext context,
    UiColors c,
    IcStylePreset preset,
    bool active,
  ) {
    final palette = IcColors.forPreset(preset, dark: true);
    return GestureDetector(
      onTap: () => unawaited(
        localPreferences?.write(PreferenceStore.localTheme, preset.id) ??
            Future.value(),
      ),
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: AnimatedContainer(
          duration: IcMotion.fast,
          width: 152,
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: active ? c.accentTint : c.surface,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: active ? c.accent : c.hairline,
              width: active ? 1.5 : 1,
            ),
            boxShadow: active ? UiShadows.sm(c) : null,
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Container(
                    width: 18,
                    height: 18,
                    decoration: BoxDecoration(
                      color: palette.surfacePage,
                      borderRadius: BorderRadius.circular(5),
                      border: Border.all(color: palette.borderInput),
                    ),
                    alignment: Alignment.center,
                    child: Container(
                      width: 8,
                      height: 8,
                      decoration: BoxDecoration(
                        color: palette.accent,
                        shape: BoxShape.circle,
                      ),
                    ),
                  ),
                  const Spacer(),
                  if (palette.isGlass)
                    const TagPill(label: 'Glass', tone: TagTone.accent),
                ],
              ),
              const SizedBox(height: 8),
              Text(
                preset.label,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(
                  12.5,
                  weight: FontWeight.w600,
                  color: c.strong,
                ),
              ),
              const SizedBox(height: 2),
              Text(
                preset.description,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: UiType.textStyle(11, color: c.muted),
              ),
            ],
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) => _node(context, root, 0);
}

class _ImmutableFieldBox extends StatefulWidget {
  const _ImmutableFieldBox({
    required this.label,
    required this.value,
    required this.placeholder,
    required this.secret,
    required this.enabled,
    this.onSubmit,
  });

  final String label;
  final String value;
  final String placeholder;
  final bool secret;
  final bool enabled;
  final VoidCallback? onSubmit;

  @override
  State<_ImmutableFieldBox> createState() => _ImmutableFieldBoxState();
}

class _ImmutableFieldBoxState extends State<_ImmutableFieldBox> {
  late final TextEditingController _controller = TextEditingController(
    text: widget.value,
  );
  final FocusNode _focus = FocusNode();

  @override
  void didUpdateWidget(_ImmutableFieldBox oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.value != widget.value && _controller.text != widget.value) {
      _controller.text = widget.value;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => UiKitField(
    label: widget.label,
    placeholder: widget.placeholder,
    controller: _controller,
    focusNode: _focus,
    secret: widget.secret,
    enabled: widget.enabled,
    onSubmitted: widget.onSubmit != null ? (_) => widget.onSubmit!() : null,
  );
}

class _InteractiveBrainSceneBox extends StatefulWidget {
  const _InteractiveBrainSceneBox({
    required this.height,
    required this.nodes,
    required this.synapses,
    this.initialSelectedId,
    this.pauseRotation = false,
    this.zoomTargetId,
    this.targetZoom = 1.65,
    required this.interactive,
    required this.showHud,
    required this.onSelectNodeIndex,
  });

  final double height;
  final List<BrainNode3D> nodes;
  final List<BrainSynapse3D> synapses;
  final String? initialSelectedId;
  final bool pauseRotation;
  final String? zoomTargetId;
  final double targetZoom;
  final bool interactive;
  final bool showHud;
  final ValueChanged<int> onSelectNodeIndex;

  @override
  State<_InteractiveBrainSceneBox> createState() =>
      _InteractiveBrainSceneBoxState();
}

class _InteractiveBrainSceneBoxState extends State<_InteractiveBrainSceneBox> {
  late String? _selectedId =
      widget.zoomTargetId ??
      widget.initialSelectedId ??
      (widget.nodes.isNotEmpty ? widget.nodes.first.id : null);
  late bool _nodeSelectedOrPaused =
      widget.pauseRotation ||
      widget.zoomTargetId != null ||
      (widget.initialSelectedId != null &&
          widget.initialSelectedId!.isNotEmpty);

  @override
  void didUpdateWidget(_InteractiveBrainSceneBox oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.zoomTargetId != widget.zoomTargetId &&
        widget.zoomTargetId != null) {
      _selectedId = widget.zoomTargetId;
      _nodeSelectedOrPaused = true;
    } else if (oldWidget.initialSelectedId != widget.initialSelectedId &&
        widget.initialSelectedId != null) {
      _selectedId = widget.initialSelectedId;
      _nodeSelectedOrPaused = true;
    }
    if (widget.pauseRotation && !_nodeSelectedOrPaused) {
      _nodeSelectedOrPaused = true;
    }
  }

  void _selectNode(BrainNode3D node) {
    setState(() {
      _selectedId = node.id;
      _nodeSelectedOrPaused = true;
    });
    final idx = widget.nodes.indexWhere((item) => item.id == node.id);
    if (idx >= 0) widget.onSelectNodeIndex(idx);
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final selectedNode = widget.nodes.firstWhere(
      (n) => n.id == _selectedId,
      orElse: () => widget.nodes.first,
    );
    final effectivePause = widget.pauseRotation || _nodeSelectedOrPaused;
    final effectiveZoomTarget = _nodeSelectedOrPaused
        ? (_selectedId ?? widget.zoomTargetId)
        : widget.zoomTargetId;

    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        SizedBox(
          height: widget.height,
          width: double.infinity,
          child: ClipRRect(
            borderRadius: BorderRadius.circular(IcRadius.card),
            child: BrainScene3D(
              nodes: widget.nodes,
              synapses: widget.synapses,
              selectedId: _selectedId,
              pauseRotation: effectivePause,
              zoomTargetId: effectiveZoomTarget,
              targetZoom: widget.targetZoom,
              interactive: widget.interactive,
              showHud: widget.showHud,
              onSelectNode: _selectNode,
            ),
          ),
        ),
        const SizedBox(height: 10),
        IcAcrylicBox(
          padding: const EdgeInsets.all(12),
          borderRadius: BorderRadius.circular(IcRadius.card),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  IcAppIcon(
                    appId: selectedNode.appId ?? selectedNode.id,
                    size: 24,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          selectedNode.label,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.text(
                            13.5,
                            weight: FontWeight.w700,
                            color: c.textStrong,
                          ),
                        ),
                        Text(
                          selectedNode.neuronUri,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.code(11.5, color: c.textMuted),
                        ),
                      ],
                    ),
                  ),
                  TagPill(label: selectedNode.moduleId, tone: TagTone.neutral),
                  const SizedBox(width: 6),
                  TagPill(
                    label: '${selectedNode.signalsPerMin} signals/min',
                    tone: TagTone.accent,
                    icon: LucideIcons.zap,
                  ),
                  const SizedBox(width: 6),
                  Btn(
                    key: ValueKey('brain3d-copy-uri-${selectedNode.id}'),
                    label: 'Copy URI',
                    small: true,
                    icon: LucideIcons.copy,
                    onTap: () {
                      Clipboard.setData(
                        ClipboardData(text: selectedNode.neuronUri),
                      );
                      showKitToast(context, 'Copied ${selectedNode.neuronUri}');
                    },
                  ),
                ],
              ),
              if (selectedNode.subtitle != null &&
                  selectedNode.subtitle!.isNotEmpty &&
                  selectedNode.subtitle != selectedNode.neuronUri) ...[
                const SizedBox(height: 6),
                Text(
                  selectedNode.subtitle!,
                  style: IcType.text(12, color: c.textBody),
                ),
              ],
              if (widget.nodes.length > 1) ...[
                const SizedBox(height: 8),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final n in widget.nodes)
                      Btn(
                        key: ValueKey('brain3d-select-${n.id}'),
                        label: n.label,
                        small: true,
                        primary: n.id == selectedNode.id,
                        onTap: () => _selectNode(n),
                      ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _InteractiveWasmBrowserFrame extends StatefulWidget {
  const _InteractiveWasmBrowserFrame({
    required this.title,
    required this.url,
    required this.executionMode,
    required this.height,
    this.subtitle,
    this.onReload,
    this.children = const [],
  });

  final String title;
  final String url;
  final String executionMode;
  final double height;
  final String? subtitle;
  final VoidCallback? onReload;
  final List<Widget> children;

  @override
  State<_InteractiveWasmBrowserFrame> createState() =>
      _InteractiveWasmBrowserFrameState();
}

class _InteractiveWasmBrowserFrameState
    extends State<_InteractiveWasmBrowserFrame> {
  int _reloadSession = 0;

  String get _appId {
    final lower = '${widget.title} ${widget.url}'.toLowerCase();
    if (lower.contains('18083') ||
        lower.contains('filmcraft') ||
        lower.contains('videocraft')) {
      return 'filmcraft';
    }
    return 'photocraft';
  }

  String get _formattedMode {
    final m = widget.executionMode.toLowerCase();
    if (m.contains('wasm')) return 'WASM Web UI';
    if (m.contains('mcp')) return 'MCP Server';
    if (m.contains('native')) return 'Native Linux CLI';
    return widget.executionMode;
  }

  void _triggerReload() {
    setState(() => _reloadSession++);
    widget.onReload?.call();
    if (mounted) {
      showKitToast(context, 'Reloaded ${widget.title} (${widget.url})');
    }
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return IcAcrylicBox(
      key: ValueKey('wasm-browser-frame-${widget.url}'),
      padding: const EdgeInsets.all(14),
      borderRadius: BorderRadius.circular(IcRadius.card),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              IcAppIcon(appId: _appId, size: 24),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      widget.title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        13.5,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                    if (widget.subtitle != null && widget.subtitle!.isNotEmpty)
                      Text(
                        widget.subtitle!,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: IcType.text(11.5, color: c.textMuted),
                      ),
                  ],
                ),
              ),
              GestureDetector(
                onTap: () {
                  Clipboard.setData(ClipboardData(text: widget.url));
                  showKitToast(context, 'Copied ${widget.url}');
                },
                child: MouseRegion(
                  cursor: SystemMouseCursors.click,
                  child: TagPill(
                    key: const ValueKey('webbrowser-url-pill'),
                    label: widget.url,
                    tone: TagTone.neutral,
                    icon: LucideIcons.globe,
                  ),
                ),
              ),
              const SizedBox(width: 6),
              TagPill(
                key: const ValueKey('webbrowser-mode-badge'),
                label: _formattedMode,
                tone: TagTone.accent,
                icon: LucideIcons.cpu,
              ),
              const SizedBox(width: 6),
              Tooltip(
                message: 'Reload WASM Studio',
                child: GestureDetector(
                  key: const ValueKey('webbrowser-reload-btn'),
                  onTap: _triggerReload,
                  child: MouseRegion(
                    cursor: SystemMouseCursors.click,
                    child: Container(
                      width: 28,
                      height: 28,
                      decoration: BoxDecoration(
                        color: c.surfaceSunk,
                        borderRadius: BorderRadius.circular(IcRadius.xs),
                        border: Border.all(color: c.borderHairline),
                      ),
                      child: Icon(
                        LucideIcons.rotateCw,
                        size: 13.5,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          Container(
            key: ValueKey('webbrowser-viewport-$_reloadSession'),
            height: widget.height,
            width: double.infinity,
            padding: const EdgeInsets.all(16),
            decoration: BoxDecoration(
              color: c.surfaceSunk,
              borderRadius: BorderRadius.circular(IcRadius.sm),
              border: Border.all(color: c.borderInput),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(LucideIcons.sparkles, size: 15, color: c.accent),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        'Embedded WASM Studio Viewport · ${widget.url}',
                        style: IcType.code(12, color: c.textStrong),
                      ),
                    ),
                    TagPill(
                      label: 'Session #${_reloadSession + 1}',
                      tone: TagTone.done,
                      icon: LucideIcons.circleCheck,
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Expanded(
                  child: Container(
                    width: double.infinity,
                    decoration: BoxDecoration(
                      color: c.surfaceCard,
                      borderRadius: BorderRadius.circular(IcRadius.xs),
                      border: Border.all(color: c.borderHairline),
                    ),
                    padding: const EdgeInsets.all(14),
                    child: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        IcAppIcon(appId: _appId, size: 36),
                        const SizedBox(height: 8),
                        Text(
                          '${widget.title} · Interactive Canvas Ready',
                          style: IcType.text(
                            13.5,
                            weight: FontWeight.w600,
                            color: c.textStrong,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          'Connected to ${widget.url} ($_formattedMode)',
                          style: IcType.text(
                            12,
                            color: c.textMuted,
                            tnum: true,
                          ),
                        ),
                        if (widget.children.isNotEmpty) ...[
                          const SizedBox(height: 10),
                          Wrap(
                            spacing: 8,
                            runSpacing: 8,
                            children: widget.children,
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
