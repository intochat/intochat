part of 'neuron_view.dart';

/// Inline presentation values never acquire their own load or watch lifecycle.
class ImmutableView extends StatelessWidget {
  const ImmutableView({
    super.key,
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
          if ((action == 'ask-ino' || action.endsWith(':ask-ino')) &&
              onActivate != null) {
            onActivate!({
              'kind': 'os_uri',
              'uri': 'os://window/open?app=ino',
              'host': 'window',
              'target': 'open',
              'query': const {'app': 'ino'},
            });
          }
          if (action.startsWith('switch-identity:') &&
              localPreferences != null) {
            final parts = action.split(':');
            if (parts.length > 1 && parts[1].isNotEmpty) {
              unawaited(
                localPreferences!.write(
                  PreferenceStore.localTheme,
                  Uri.decodeComponent(parts[1]),
                ),
              );
            }
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
        return _StudioNavRailItem(
          label: label,
          icon: iconSlug != null ? uiIcon(iconSlug) : null,
          selected: selected,
          badge: badge,
          onTap: actChild != null ? _dispatchButton(context, actChild) : null,
        );

      case 'statcard':
        final statId = meta['id'] as String? ?? '';
        final label = meta['label'] as String? ?? text;
        final value = meta['value'] as String? ?? '0';
        final delta = meta['delta'] as String?;
        final iconSlug = meta['icon'] as String?;
        final appIconId = statId.startsWith('ico-')
            ? statId.substring(4)
            : (iconSlug != null && iconSlug.startsWith('app:')
                  ? iconSlug.substring(4)
                  : null);
        final tagTone = switch ((meta['tone'] as String? ?? 'neutral')
            .toLowerCase()) {
          'accent' => TagTone.accent,
          'done' || 'ok' || 'success' => TagTone.done,
          'working' || 'warn' || 'warning' => TagTone.working,
          'error' || 'danger' => TagTone.error,
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
                  if (appIconId != null && appIconId.isNotEmpty) ...[
                    IcAppIcon(appId: appIconId, size: 32),
                    const SizedBox(width: 10),
                  ] else if (iconSlug != null && uiIcon(iconSlug) != null) ...[
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
                  20,
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
        return LayoutBuilder(
          builder: (context, constraints) {
            final effectiveCols = constraints.maxWidth < 560
                ? 1
                : (constraints.maxWidth < 860 && cols > 2 ? 2 : cols);
            return Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: gap,
              children: [
                for (var i = 0; i < widgets.length; i += effectiveCols)
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    spacing: gap,
                    children: [
                      for (var col = 0; col < effectiveCols; col++)
                        Expanded(
                          child: i + col < widgets.length
                              ? widgets[i + col]
                              : const SizedBox.shrink(),
                        ),
                    ],
                  ),
              ],
            );
          },
        );

      case 'surface':
        final surfaceKind = meta['kind'] as String? ?? 'acrylic';
        final subtitle = meta['subtitle'] as String?;
        final gap = (meta['gap'] as num?)?.toDouble() ?? 12.0;
        final pad = (meta['padding'] as num?)?.toDouble() ?? 16.0;
        final inspectBtn = children.where((ch) {
          if (ch['kind'] != 1) return false;
          final act = ch['action'] as String? ?? '';
          final lbl = ch['text'] as String? ?? '';
          return act.startsWith('select:') && lbl.startsWith('Inspect ');
        }).firstOrNull;
        final firstBadgeIcon = children
            .where((ch) => ch['kind'] == 10 && ch['icon'] is String)
            .map((ch) => ch['icon'] as String)
            .firstOrNull;
        final headerAppId = text.startsWith('UI Gallery')
            ? 'gallery'
            : (text.startsWith('Creator Studio')
                  ? 'creator'
                  : (text.startsWith('DigitalBrain Profile') ? 'post' : null));
        final inner = Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: gap,
          children: [
            if (text.isNotEmpty || (subtitle != null && subtitle.isNotEmpty))
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (headerAppId != null) ...[
                    IcAppIcon(appId: headerAppId, size: 36),
                    const SizedBox(width: 12),
                  ] else if (inspectBtn != null &&
                      firstBadgeIcon != null &&
                      uiIcon(firstBadgeIcon) != null) ...[
                    Container(
                      width: 34,
                      height: 34,
                      decoration: BoxDecoration(
                        color: IcTheme.of(context).accentTint,
                        borderRadius: BorderRadius.circular(9),
                      ),
                      child: Icon(
                        uiIcon(firstBadgeIcon),
                        size: 16,
                        color: IcTheme.of(context).accent,
                      ),
                    ),
                    const SizedBox(width: 10),
                  ],
                  Expanded(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      crossAxisAlignment: CrossAxisAlignment.start,
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
                        if (subtitle != null && subtitle.isNotEmpty) ...[
                          if (text.isNotEmpty) const SizedBox(height: 2),
                          Text(
                            subtitle,
                            style: UiType.textStyle(12, color: c.muted),
                          ),
                        ],
                      ],
                    ),
                  ),
                ],
              ),
            ...widgets,
          ],
        );
        final surfaceWidget = surfaceKind == 'recessed'
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
                child: UiKitSurface(
                  padding: EdgeInsets.all(pad),
                  color: surfaceKind == 'elevated' ? c.elevated : null,
                  shadows: surfaceKind == 'elevated' ? UiShadows.md(c) : null,
                  borderColor: surfaceKind == 'elevated'
                      ? c.accent.withValues(alpha: 0.28)
                      : null,
                  child: inner,
                ),
              );
        if (inspectBtn != null) {
          final onTapInspect = _dispatchButton(context, inspectBtn);
          if (onTapInspect != null) {
            return MouseRegion(
              cursor: SystemMouseCursors.click,
              child: GestureDetector(
                behavior: HitTestBehavior.opaque,
                onTap: onTapInspect,
                child: surfaceWidget,
              ),
            );
          }
        }
        return surfaceWidget;

      case 'textfield':
        final fieldName = meta['name'] as String? ?? '';
        final hideLabel = const {
          'gallery-search',
          'settings-search',
          'search',
          'prompt',
        }.contains(fieldName);
        final submitChild = _findChildByKeySuffix(children, '.submit');
        // A field named by a neuron key ({brain}/apps/{app}/{field}) is that textfield neuron: each change goes to it.
        final fieldNeuron = fieldName.contains('/');
        return _ImmutableFieldBox(
          label: hideLabel ? '' : fieldName.split('/').last,
          onChanged: enabled && fieldNeuron
              ? (typedValue) => unawaited(
                  dispatch({
                    'kind': 'textfield',
                    'name': fieldName,
                    'value': typedValue,
                  }).catchError((Object _) {}),
                )
              : null,
          value: meta['value'] as String? ?? '',
          placeholder: meta['placeholder'] as String? ?? fieldName,
          secret: meta['secret'] == true,
          enabled: enabled,
          onSubmit: _childAction(context, children, '.submit'),
          onSubmittedValue: enabled && submitChild != null
              ? (typedValue) {
                  FocusManager.instance.primaryFocus?.unfocus();
                  final rawAction = submitChild['action'] as String? ?? '';
                  String resolvedAction = rawAction;
                  if (rawAction.startsWith('filter:')) {
                    final segs = rawAction.split(':');
                    final cat = segs.length > 1 ? segs[1] : 'all';
                    resolvedAction =
                        'filter:$cat:${Uri.encodeComponent(typedValue.trim())}';
                  }
                  unawaited(
                    dispatch({
                      'kind': 'view',
                      'name': name,
                      'revision': generation,
                      'field': submitChild['key'],
                      'action': resolvedAction,
                      'operationId': _operationId(),
                    }).catchError((Object _) {}),
                  );
                }
              : null,
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
        final toneRaw = (meta['tone'] as String? ?? 'info').toLowerCase();
        final severity = switch (toneRaw) {
          'danger' || 'error' => 'error',
          'warning' || 'warn' => 'warning',
          'success' || 'done' || 'ok' => 'success',
          _ => 'info',
        };
        final infoTitle = meta['title'] as String? ?? text;
        final infoBody = meta['body'] as String? ?? '';
        final actionChild = _findChildByKeySuffix(children, '.action');
        if (severity == 'error' && actionChild != null) {
          final actLabel = actionChild['text'] as String? ?? 'Retry';
          return ErrorLine(
            error: InoError(
              'info-error',
              LucideIcons.triangleAlert,
              infoBody.isNotEmpty ? '$infoTitle — $infoBody' : infoTitle,
              action: actLabel,
            ),
            onAction: _dispatchButton(context, actionChild),
          );
        }
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            UiInfoBar(severity: severity, title: infoTitle, body: infoBody),
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
        final approvalId = meta['id'] as String? ?? '';
        final verb = meta['verb'] as String? ?? 'Execute';
        final target = meta['target'] as String? ?? text;
        final sourceApp = meta['sourceApp'] as String? ?? 'Ino';
        final sourceUri = meta['sourceNeuronUri'] as String?;
        final scopeUri = meta['scopeUri'] as String?;
        final rawRows = (meta['rows'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        final stateRaw = (meta['state'] as String? ?? 'pending').toLowerCase();
        if (approvalId.startsWith('change')) {
          final changeState = switch (stateRaw) {
            'approved' ||
            'applied' ||
            'settled' ||
            'done' => ChangeState.applied,
            'rejected' || 'denied' || 'cancelled' => ChangeState.cancelled,
            _ => ChangeState.pending,
          };
          return ChangePreviewCard(
            source: _cardSource(context, sourceApp),
            data: ChangeData(
              objectLabel: verb,
              name: scopeUri ?? target,
              subtitle: meta['rememberHint'] as String?,
              systemName: sourceApp,
              fields: [
                for (final r in rawRows)
                  ChangeField(
                    label: '${r['key'] ?? ''}',
                    oldValue: r['oldValue'] as String?,
                    newValue: '${r['value'] ?? ''}',
                  ),
              ],
            ),
            state: changeState,
            onApply: _childAction(context, children, '.approve'),
            onCancel: _childAction(context, children, '.reject'),
          );
        }
        final rows = [
          for (final r in rawRows)
            ApprovalRow(
              label: '${r['key'] ?? ''}',
              oldValue: (r['oldValue'] as String?)?.isNotEmpty == true
                  ? r['oldValue'] as String
                  : 'current',
              newValue: '${r['value'] ?? ''}',
              amount: r['amount'] as String?,
            ),
          if (sourceUri != null && sourceUri.isNotEmpty && rawRows.isEmpty)
            ApprovalRow(
              label: 'source',
              oldValue: 'neuron',
              newValue: sourceUri,
            ),
        ];
        final approvalState = switch (stateRaw) {
          'approved' || 'settled' || 'done' => ApprovalState.approved,
          'rejected' || 'denied' => ApprovalState.rejected,
          'saving' => ApprovalState.saving,
          'error' => ApprovalState.error,
          _ => ApprovalState.pending,
        };
        final fullTitle = target.isNotEmpty && target != verb
            ? '$verb $target'.trim()
            : verb;
        return ApprovalCard(
          source: _cardSource(context, sourceApp),
          data: ApprovalData(
            title: fullTitle,
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
        final recordKind = meta['kind'] as String? ?? 'Record';
        final recordTitle = meta['title'] as String? ?? text;
        final recordSubtitle = meta['subtitle'] as String?;
        final linkLabel = meta['linkLabel'] as String?;
        final rawFields = (meta['fields'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        final openAction = _childAction(context, children, '.open');
        if (recordKind == 'Smart App Manifest') {
          final isInstalled = (linkLabel ?? '').toLowerCase() == 'installed';
          var handle = 'nordlys/app';
          var version = 'v1.4.0';
          for (final f in rawFields) {
            final k = '${f['key'] ?? ''}'.toLowerCase();
            final v = '${f['value'] ?? ''}';
            if (k.contains('contract') || k.contains('handle')) {
              handle = v;
            } else if (k.contains('version') || k.contains('gate')) {
              version = v;
            }
          }
          return AppInstallCard(
            source: _cardSource(context, 'IntoChat Hub'),
            data: AppInstallData(
              name: recordTitle,
              handle: handle,
              publisher: 'IntoChat DigitalBrain',
              version: version,
              summary: recordSubtitle ?? 'Compiled Smart App',
              icon: const IcAppIcon(appId: 'datavisualizer', size: 44),
            ),
            state: isInstalled
                ? AppInstallState.installed
                : AppInstallState.pending,
            onReview: openAction,
            onDismiss: openAction,
            onOpen: openAction,
          );
        }
        if (recordKind == 'Episodic Memory') {
          final isSaved = (linkLabel ?? '').toLowerCase() == 'saved';
          return RememberCard(
            source: _cardSource(context, 'Episodic Memory'),
            data: RememberData(
              text: recordTitle,
              kind: recordSubtitle ?? 'MemoryNeuron · Semantic Recall',
            ),
            state: isSaved ? RememberState.saved : RememberState.pending,
            onSave: openAction,
            onSkip: openAction,
          );
        }
        if (recordKind == 'Files Vault') {
          var size = '412 KB';
          var note = 'Updated close date and attached ledger table.';
          for (final f in rawFields) {
            final k = '${f['key'] ?? ''}'.toLowerCase();
            final v = '${f['value'] ?? ''}';
            if (k == 'size') size = v;
            if (k == 'note' || k == 'path') note = v;
          }
          final fileRef = FileRef(
            name: recordTitle,
            path: 'neuron://nordlys/storage.files/$recordTitle',
            icon: LucideIcons.fileCheck,
            size: size,
            where: recordSubtitle ?? '30 Projects › Nordlys AS',
          );
          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            spacing: 10,
            children: [
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  FileChip(file: fileRef, onTap: openAction),
                  const FileChip(
                    file: FileRef(
                      name: 'nordlys-arr-2026.csv',
                      path:
                          'neuron://nordlys/storage.files/nordlys-arr-2026.csv',
                      icon: LucideIcons.fileSpreadsheet,
                      size: '48 KB',
                      where: '30 Projects › Nordlys AS',
                    ),
                  ),
                ],
              ),
              FileCard(
                source: _cardSource(context, 'Files Vault'),
                data: FileCardData(file: fileRef, note: note),
                state: FileCardState.saved,
                onOpen: openAction,
                onReveal: openAction,
              ),
            ],
          );
        }
        final sourceApp = meta['sourceApp'] as String? ?? 'DigitalBrain';
        return RecordCard(
          source: _cardSource(context, sourceApp),
          data: RecordData(
            objectLabel: recordKind,
            name: recordTitle,
            subtitle: recordSubtitle,
            fields: [
              for (final f in rawFields)
                RecordField(
                  '${f['key'] ?? ''}',
                  '${f['value'] ?? ''}',
                  warn:
                      '${f['value'] ?? ''}'.toLowerCase().contains('ago') ||
                      '${f['value'] ?? ''}'.toLowerCase().contains('overdue'),
                ),
            ],
            linkLabel: linkLabel != null && linkLabel.isNotEmpty
                ? linkLabel
                : 'Open in $sourceApp',
          ),
          onOpen: openAction,
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
        var maxCoord = 1.0;
        for (final n in rawNodes) {
          final rx = ((n['x'] as num?)?.toDouble() ?? 0.0).abs();
          final ry = ((n['y'] as num?)?.toDouble() ?? 0.0).abs();
          final rz = ((n['z'] as num?)?.toDouble() ?? 0.0).abs();
          maxCoord = max(maxCoord, max(rx, max(ry, rz)));
        }
        final coordScale = maxCoord > 1.5 ? max(120.0, maxCoord * 1.15) : 1.0;
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
                    ? coordScale
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
              final kindRaw = '${n['kind'] ?? 'app'}'.toLowerCase();
              final nodeKind = switch (kindRaw) {
                'assistant' || 'run' => BrainNodeKind.assistant,
                'storage' || 'secret' => BrainNodeKind.storage,
                'compute' || 'neuron' => BrainNodeKind.compute,
                'model' => BrainNodeKind.model,
                'workflow' || 'operation' || 'signal' => BrainNodeKind.workflow,
                _ => BrainNodeKind.app,
              };
              final explicitAppId = n['appId'] as String?;
              final resolvedAppId =
                  (explicitAppId != null && explicitAppId.isNotEmpty)
                  ? explicitAppId
                  : (nodeId.startsWith('app.')
                        ? nodeId.substring(4)
                        : (nodeKind == BrainNodeKind.app ? nodeId : null));
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
                kind: nodeKind,
                moduleId: '${n['category'] ?? 'DigitalBrain'}',
                subtitle: n['subtitle'] as String?,
                signalsPerMin: sigs,
                appId: resolvedAppId,
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

      case 'markdraw':
        final activeDoc =
            meta['activeDocument'] as String? ?? '/work/architecture.markdraw';
        final format = meta['format'] as String? ?? 'markdraw';
        final rawContent = meta['content'] as String? ?? '';
        final rev = (meta['revision'] as num?)?.toInt() ?? 1;
        final statusText = meta['status'] as String? ?? '';
        final lastExport = meta['lastExportPath'] as String?;
        final cardHeight = (meta['height'] as num?)?.toDouble() ?? 540.0;
        return _InteractiveMarkdrawStudioCard(
          key: ValueKey('markdraw-studio-$activeDoc'),
          title:
              (meta['title'] as String?) ??
              (text.isNotEmpty ? text : 'Markdraw Studio'),
          activeDocument: activeDoc,
          format: format,
          content: rawContent,
          revision: rev,
          status: statusText,
          lastExportPath: lastExport,
          height: cardHeight,
          onSyncScene: (serializedContent, docPath) {
            final encoded = Uri.encodeComponent(serializedContent);
            unawaited(
              dispatch({
                'kind': 'view',
                'name': 'apps/markdraw/view',
                'revision': generation,
                'field': 'markdraw-sync',
                'action': 'sync-scene:$encoded',
                'operationId': _operationId(),
              }).catchError((Object _) {}),
            );
          },
          onExport: (exportFormat) {
            unawaited(
              dispatch({
                'kind': 'view',
                'name': 'apps/markdraw/view',
                'revision': generation,
                'field': 'markdraw-export',
                'action': 'export:$exportFormat',
                'operationId': _operationId(),
              }).catchError((Object _) {}),
            );
          },
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
        if (const {'ino-workspace', 'ino-main'}.contains(node['key'])) {
          content = _buildInoChatDesktopView(context, node, children, depth);
          break;
        }
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
        if (node['kind'] == 3 &&
            node['key'] == 'view' &&
            children.length == 2 &&
            children[0]['key'] == 'side' &&
            children[1]['key'] == 'root') {
          final c = UiColors.of(context);
          final sideWidth = switch ((node['extents'] as List?)?.elementAtOrNull(
            0,
          )) {
            final num w when w > 1 => w.toDouble(),
            _ => 260.0,
          };
          content = LayoutBuilder(
            builder: (context, constraints) {
              if (constraints.maxWidth < 720) {
                return SingleChildScrollView(
                  padding: const EdgeInsets.all(14),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    spacing: 14,
                    children: [widgets[0], widgets[1]],
                  ),
                );
              }
              return Container(
                color: c.page,
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Container(
                      width: sideWidth,
                      decoration: BoxDecoration(
                        color: c.recessed,
                        border: Border(right: BorderSide(color: c.hairline)),
                      ),
                      child: Padding(
                        padding: const EdgeInsets.all(12),
                        child: widgets[0],
                      ),
                    ),
                    Expanded(
                      child: Padding(
                        padding: const EdgeInsets.fromLTRB(22, 18, 22, 28),
                        child: widgets[1],
                      ),
                    ),
                  ],
                ),
              );
            },
          );
          break;
        }
        if (node['kind'] == 3 && node['key'] == 'breadcrumbs') {
          content = _buildWinUiBreadcrumbBar(context, children, widgets);
          break;
        }
        if (node['kind'] == 3 &&
            children.length == 2 &&
            extents.length == 2 &&
            extents[0] is num &&
            (extents[0] as num) >= 400 &&
            (extents[1] as num? ?? 0) == 0) {
          final splitGap = (node['gap'] as num? ?? 14).toDouble().clamp(
            0.0,
            32.0,
          );
          final trailingWidth = (extents[0] as num) >= 500 ? 284.0 : 360.0;
          content = LayoutBuilder(
            builder: (context, constraints) {
              if (constraints.maxWidth < 760) {
                return Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  spacing: splitGap,
                  children: [widgets[0], widgets[1]],
                );
              }
              return Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(child: widgets[0]),
                  SizedBox(width: splitGap),
                  SizedBox(width: trailingWidth, child: widgets[1]),
                ],
              );
            },
          );
          break;
        }
        if (node['kind'] == 3 &&
            (const {'toolbar', 'navigation'}.contains(node['key']) ||
                ((node['extents'] as List? ?? const []).isEmpty &&
                    children.isNotEmpty &&
                    children.every(
                      (ch) => ch['kind'] == 1 || ch['kind'] == 10,
                    )))) {
          final rowGap = (node['gap'] as num? ?? 6).toDouble().clamp(0.0, 32.0);
          content = Wrap(
            spacing: rowGap,
            runSpacing: rowGap,
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
        final pillMeta = _tryParseUiMeta(node['detail']);
        if (pillMeta != null && pillMeta['_ui'] == 'tagpill') {
          final pillTone = switch ((pillMeta['tone'] as String? ?? 'neutral')
              .toLowerCase()) {
            'accent' => TagTone.accent,
            'done' || 'ok' || 'success' => TagTone.done,
            'working' || 'warn' || 'warning' => TagTone.working,
            'error' || 'danger' => TagTone.error,
            _ => TagTone.neutral,
          };
          final iconSlug = node['icon'] is String
              ? node['icon'] as String
              : null;
          content = TagPill(
            label: text,
            tone: pillTone,
            icon: iconSlug != null ? uiIcon(iconSlug) : null,
          );
        } else {
          content = ViewBadge(
            text: text,
            tone: node['tone'],
            icon: node['icon'] is String ? node['icon'] as String : null,
          );
        }
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
    String? previewUrl;
    String? previewKind;

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
            } else if (pk == 'preview-url') {
              previewUrl = pc['text'] as String?;
            } else if (pk == 'preview-kind') {
              previewKind = pc['text'] as String?;
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

    final effectiveOnAskIno =
        onAskIno ??
        (enabled
            ? () {
                final targetName =
                    selectedName ??
                    items.where((i) => i.selected).firstOrNull?.name ??
                    items.firstOrNull?.name ??
                    activeWhereLabel();
                final targetUri =
                    selectedNeuronUri ??
                    activeNeuronUri ??
                    'neuron://owner/local-disk.files';
                final basePrompt =
                    'Explain and summarize $targetName ($targetUri)';
                final fullPrompt =
                    previewText != null && previewText!.trim().isNotEmpty
                    ? '$basePrompt\n\n[File Preview]\n${previewText!.length <= 1200 ? previewText! : previewText!.substring(0, 1200)}'
                    : basePrompt;
                unawaited(_submitToInoComposer(fullPrompt));
                if (onActivate != null) {
                  onActivate!({
                    'kind': 'os_uri',
                    'uri': 'os://window/open?app=ino',
                    'host': 'window',
                    'target': 'open',
                    'query': const {'app': 'ino'},
                  });
                }
              }
            : null);

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
      onAskIno: effectiveOnAskIno,
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
      previewUrl: previewUrl,
      previewKind: previewKind,
    );
  }

  Future<void> _submitToInoComposer(String rawText) async {
    final trimmed = rawText.trim();
    if (trimmed.isEmpty || !enabled) return;
    final appsIdx = name.indexOf('/apps/');
    final brainPrefix = appsIdx > 0 ? name.substring(0, appsIdx) : 'owner';
    final appKey = '$brainPrefix/apps/ino';
    final composerName = '$appKey/composer';
    try {
      await dispatch({
        'kind': 'composer',
        'name': composerName,
        'action': 'edit',
        'context': appKey,
        'selectionRevision': 1,
        'value': trimmed,
      });
      await dispatch({
        'kind': 'composer',
        'name': composerName,
        'action': 'submit',
        'context': appKey,
        'selectionRevision': 1,
        'value': trimmed,
        'operationId': _operationId(),
      });
    } catch (_) {}
  }

  Widget _buildInoChatDesktopView(
    BuildContext context,
    Map node,
    List<Map> children,
    int depth,
  ) {
    Map? sideNode;
    Map? mainNode;
    final rootKey = node['key'] as String? ?? '';
    if (rootKey == 'ino-workspace') {
      for (final c in children) {
        final k = c['key'] as String? ?? '';
        if (k == 'ino-side' || k == 'side') sideNode = c;
        if (k == 'ino-main' || k == 'root') mainNode = c;
      }
    } else {
      mainNode = node;
    }

    // 1. Parse sidebar sessions & New Session button
    VoidCallback? onNewSession;
    final pinnedSessions = <_InoSessionNavItem>[];
    final todaySessions = <_InoSessionNavItem>[];
    var currentGroup = 'Pinned';
    var activeConversationId = 'main';

    if (sideNode != null) {
      for (final sc
          in (sideNode['children'] as List? ?? const []).whereType<Map>()) {
        final sk = sc['key'] as String? ?? '';
        if (sc['kind'] == 4 && sk.endsWith('.0')) {
          for (final sub
              in (sc['children'] as List? ?? const []).whereType<Map>()) {
            if (sub['kind'] == 1 && (sub['action'] as String? ?? '') == 'new') {
              onNewSession = _dispatchButton(context, sub);
            }
          }
        } else if (sc['kind'] == 12) {
          final divLabel = (sc['text'] as String? ?? '').toUpperCase();
          currentGroup = divLabel.contains('TODAY') ? 'Today' : 'Pinned';
        } else if (sc['kind'] == 4) {
          final meta = _tryParseUiMeta(sc['detail']);
          if (meta?['_ui'] == 'navitem') {
            final label =
                meta!['label'] as String? ?? sc['text'] as String? ?? '';
            final iconSlug = meta['icon'] as String? ?? 'message-square';
            final selected = meta['selected'] == true;
            final badge = meta['badge'] as String?;
            final scChildren = (sc['children'] as List? ?? const [])
                .whereType<Map>()
                .toList();
            final actChild =
                _findChildByKeySuffix(scChildren, '.act') ??
                (scChildren.isNotEmpty ? scChildren.first : null);
            final navItem = _InoSessionNavItem(
              key: sk,
              label: label,
              icon: uiIcon(iconSlug) ?? LucideIcons.messageSquare,
              selected: selected,
              badge: badge,
              onTap: actChild != null
                  ? _dispatchButton(context, actChild)
                  : () => unawaited(_submitToInoComposer('/scenario $sk')),
            );
            if (currentGroup == 'Today') {
              todaySessions.add(navItem);
            } else {
              pinnedSessions.add(navItem);
            }
          }
        } else if (sc['kind'] == 9) {
          final entries = (sc['children'] as List? ?? const [])
              .whereType<Map>()
              .toList();
          if (entries.isNotEmpty) {
            final ek = entries.first['key'] as String? ?? '';
            final dot = ek.lastIndexOf('.');
            if (dot >= 0 && dot + 1 < ek.length) {
              activeConversationId = ek.substring(dot + 1);
            }
          }
        }
      }
    }

    onNewSession ??= enabled
        ? () => unawaited(_submitToInoComposer('/new'))
        : null;

    // 2. Parse main conversation header, cards, docked waiting banner, and turns
    var sessionTitle = 'New chat';
    var statusText = 'Ready';
    var isListening = false;
    String? connectedDb;
    final parsedCards = <_InoParsedCard>[];
    final parsedTurns = <_InoParsedTurn>[];
    VoidCallback? onConfirmWaiting;
    VoidCallback? onDismissWaiting;

    if (mainNode != null) {
      final mainChildren = (mainNode['children'] as List? ?? const [])
          .whereType<Map>()
          .toList();
      String? pendingCardKind;
      String? pendingCardStatus;
      String? pendingCardTitle;
      String? pendingCardUri;

      for (var i = 0; i < mainChildren.length; i++) {
        final mc = mainChildren[i];
        if (i == 0 && mc['kind'] == 4) {
          sessionTitle = (mc['text'] as String?)?.isNotEmpty == true
              ? mc['text'] as String
              : 'New chat';
          final hdrChildren = (mc['children'] as List? ?? const [])
              .whereType<Map>()
              .toList();
          if (hdrChildren.isNotEmpty && hdrChildren[0]['kind'] == 3) {
            for (final pill
                in (hdrChildren[0]['children'] as List? ?? const [])
                    .whereType<Map>()) {
              final pt = pill['text'] as String? ?? '';
              final pi = pill['icon'] as String? ?? '';
              if (pt == 'Listening') isListening = true;
              if (pi == 'database') connectedDb = pt;
            }
          }
          if (hdrChildren.length > 1 && hdrChildren[1]['kind'] == 0) {
            statusText = hdrChildren[1]['text'] as String? ?? statusText;
          }
          continue;
        }

        if (mc['kind'] == 0) {
          final rawText = mc['text'] as String? ?? '';
          final match = RegExp(
            r'^\[([^:]+):([^\]]+)\]\s+(.+?)\s+\((neuron://[^|\s]+)',
          ).firstMatch(rawText);
          if (match != null) {
            pendingCardKind = match.group(1)!;
            pendingCardStatus = match.group(2)!;
            pendingCardTitle = match.group(3)!;
            pendingCardUri = match.group(4)!;
          }
          continue;
        }

        if (mc['kind'] == 4) {
          final meta = _tryParseUiMeta(mc['detail']);
          final uiKind = meta?['_ui'] as String? ?? '';
          final cardText = mc['text'] as String? ?? '';

          if (uiKind == 'surface' && cardText.startsWith('Waiting on you:')) {
            final surfaceChildren = (mc['children'] as List? ?? const [])
                .whereType<Map>()
                .toList();
            if (surfaceChildren.isNotEmpty && surfaceChildren[0]['kind'] == 3) {
              for (final btn
                  in (surfaceChildren[0]['children'] as List? ?? const [])
                      .whereType<Map>()) {
                if (btn['kind'] == 1) {
                  final bt = btn['text'] as String? ?? '';
                  if (bt.startsWith('Confirm')) {
                    onConfirmWaiting = _dispatchButton(context, btn);
                  } else if (bt.startsWith('Dismiss')) {
                    onDismissWaiting = _dispatchButton(context, btn);
                  }
                }
              }
            }
            continue;
          }

          if (uiKind == 'surface' &&
              (meta?['kind'] as String? ?? '') == 'elevated') {
            final turnChildren = (mc['children'] as List? ?? const [])
                .whereType<Map>()
                .toList();
            var routeHint = meta?['subtitle'] as String? ?? 'agent';
            String? recalledBadge;
            if (turnChildren.isNotEmpty && turnChildren[0]['kind'] == 3) {
              final pills = (turnChildren[0]['children'] as List? ?? const [])
                  .whereType<Map>()
                  .toList();
              if (pills.length > 1) {
                routeHint = pills[1]['text'] as String? ?? routeHint;
              }
              if (pills.length > 2) {
                recalledBadge = pills[2]['text'] as String?;
              }
            }
            final userMsg = turnChildren.length > 1
                ? (turnChildren[1]['text'] as String? ?? cardText)
                : cardText;
            final replyMsg = turnChildren.length > 2
                ? (turnChildren[2]['text'] as String? ?? '')
                : '';
            parsedTurns.add(
              _InoParsedTurn(
                userMessage: userMsg,
                reply: replyMsg,
                routeHint: routeHint,
                recalledBadge: recalledBadge,
              ),
            );
            continue;
          }

          if (const {
            'approval',
            'choice',
            'draft',
            'secret',
            'record',
            'datatable',
            'chart',
            'webbrowser',
            'markdraw',
            'neuronRef',
          }.contains(uiKind)) {
            final k = pendingCardKind ?? uiKind;
            final st =
                pendingCardStatus ??
                (meta?['state'] as String?) ??
                (meta?['sent'] == true || meta?['saved'] == true
                    ? 'approved'
                    : 'ready');
            final isWaiting =
                st.toLowerCase() == 'pending' &&
                const {
                  'approval',
                  'choice',
                  'draft',
                  'secret',
                  'app-install',
                }.contains(k.toLowerCase());
            parsedCards.add(
              _InoParsedCard(
                key: mc['key'] as String? ?? 'card-$i',
                kind: k,
                status: st,
                title: pendingCardTitle ?? cardText,
                sourceUri: pendingCardUri,
                isWaiting: isWaiting,
                widget: _node(context, mc, depth + 1),
              ),
            );
            pendingCardKind = null;
            pendingCardStatus = null;
            pendingCardTitle = null;
            pendingCardUri = null;
          }
        }
      }
    }

    return _InoChatDesktopWorkspace(
      activeConversationId: activeConversationId,
      sessionTitle: sessionTitle,
      statusText: statusText,
      isListening: isListening,
      connectedDb: connectedDb,
      pinnedSessions: pinnedSessions,
      todaySessions: todaySessions,
      cards: parsedCards,
      turns: parsedTurns,
      markdown: markdown,
      onNewSession: onNewSession,
      onConfirmWaiting: onConfirmWaiting,
      onDismissWaiting: onDismissWaiting,
      onSubmitPrompt: (prompt) => unawaited(_submitToInoComposer(prompt)),
      onOpenApp: (targetApp) {
        if (onActivate != null) {
          onActivate!({
            'kind': 'os_uri',
            'uri': 'os://window/open?app=$targetApp',
            'host': 'window',
            'target': 'open',
            'query': {'app': targetApp},
          });
        }
      },
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

  static String _extractStudioUrl(Map node, String fallbackUrl) {
    final detail = node['detail'];
    if (detail is String && detail.contains('"_ui":"webbrowser"')) {
      final meta = _tryParseUiMeta(detail);
      final u = meta?['url'] as String?;
      if (u != null && u.trim().isNotEmpty) return u.trim();
    }
    if (node['key'] == 'browser' && node['text'] is String) {
      final t = (node['text'] as String).trim();
      if (t.startsWith('http://') || t.startsWith('https://')) return t;
    }
    final children = node['children'];
    if (children is List) {
      for (final c in children) {
        if (c is Map) {
          final found = _extractStudioUrl(c, '');
          if (found.isNotEmpty) return found;
        }
      }
    }
    return fallbackUrl;
  }

  @override
  Widget build(BuildContext context) {
    if ((name.endsWith('/apps/photocraft/view') ||
            name.endsWith('/apps/filmcraft/view')) &&
        root['key'] != 'app-body') {
      final isFilm = name.endsWith('/apps/filmcraft/view');
      final defaultUrl = isFilm
          ? 'http://localhost:18083'
          : 'http://localhost:18082';
      final defaultTitle = isFilm
          ? 'FilmCraft WASM Studio'
          : 'PhotoCraft WASM Studio';
      final url = _extractStudioUrl(root, defaultUrl);
      return SizedBox.expand(
        child: _InteractiveWasmBrowserFrame(
          key: ValueKey('studio-wasm-frame-$name'),
          title: defaultTitle,
          url: url,
          executionMode: 'wasm-ui',
          height: 0,
        ),
      );
    }
    return _node(context, root, 0);
  }
}

class _ImmutableFieldBox extends StatefulWidget {
  const _ImmutableFieldBox({
    required this.label,
    required this.value,
    required this.placeholder,
    required this.secret,
    required this.enabled,
    this.onSubmit,
    this.onSubmittedValue,
    this.onChanged,
  });

  final String label;
  final String value;
  final String placeholder;
  final bool secret;
  final bool enabled;
  final VoidCallback? onSubmit;
  final ValueChanged<String>? onSubmittedValue;
  final ValueChanged<String>? onChanged;

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
    onChanged: widget.onChanged,
    onSubmitted: widget.onSubmittedValue != null
        ? (val) => widget.onSubmittedValue!(val)
        : widget.onSubmit != null
        ? (_) => widget.onSubmit!()
        : null,
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
  bool _showInspector = false;
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
                ],
              ),
              const SizedBox(height: 8),
              Wrap(
                spacing: 6,
                runSpacing: 6,
                children: [
                  Btn(
                    key: ValueKey('brain3d-inspect-state-${selectedNode.id}'),
                    label: _showInspector ? 'Hide State' : 'Inspect State',
                    small: true,
                    primary: _showInspector,
                    icon: LucideIcons.scanLine,
                    onTap: () {
                      setState(() {
                        _showInspector = !_showInspector;
                      });
                    },
                  ),
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
              if (_showInspector) ...[
                const SizedBox(height: 10),
                Container(
                  width: double.infinity,
                  padding: const EdgeInsets.all(10),
                  decoration: BoxDecoration(
                    color: c.surfaceCard,
                    borderRadius: BorderRadius.circular(IcRadius.sm),
                    border: Border.all(color: c.borderHairline),
                  ),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Icon(LucideIcons.terminal, size: 14, color: c.accent),
                          const SizedBox(width: 6),
                          Text(
                            'LIVE NEURON STATE',
                            style: IcType.text(
                              11,
                              weight: FontWeight.w700,
                              color: c.accent,
                            ),
                          ),
                          const Spacer(),
                          const TagPill(
                            label: 'IInspectableNeuron',
                            tone: TagTone.accent,
                          ),
                        ],
                      ),
                      const SizedBox(height: 8),
                      Container(
                        width: double.infinity,
                        padding: const EdgeInsets.all(8),
                        decoration: BoxDecoration(
                          color: c.surfaceSunk,
                          borderRadius: BorderRadius.circular(IcRadius.sm),
                        ),
                        child: SelectableText(
                          '{\n  "neuron": "${selectedNode.neuronUri}",\n  "type": "${selectedNode.moduleId}",\n  "status": "active",\n  "signalsPerMin": ${selectedNode.signalsPerMin}\n}',
                          style: IcType.code(11.5, color: c.textStrong),
                        ),
                      ),
                    ],
                  ),
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
    super.key,
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
        lower.contains('18111') ||
        lower.contains('filmcraft') ||
        lower.contains('videocraft')) {
      return 'filmcraft';
    }
    return 'photocraft';
  }

  String get _resolvedUrl {
    final raw = widget.url.trim();
    if (kIsWeb) {
      final base = Uri.base;
      final isLocalOrEmpty =
          raw.isEmpty || raw.contains('localhost') || raw.contains('127.0.0.1');
      if (isLocalOrEmpty && (base.scheme == 'http' || base.scheme == 'https')) {
        final studioPath = _appId == 'filmcraft'
            ? '/artcraft/filmcraft/index.html'
            : '/artcraft/photocraft/index.html';
        if (base.host != 'localhost' && base.host != '127.0.0.1') {
          return '${base.origin}$studioPath';
        }
        return _appId == 'filmcraft'
            ? 'http://localhost:18083/'
            : 'http://localhost:18082/';
      }
    }
    return raw;
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
    final resolvedUrl = _resolvedUrl;
    return LayoutBuilder(
      builder: (context, constraints) {
        final boundedHeight = constraints.hasBoundedHeight;
        final fallbackHeight = widget.height > 0 ? widget.height : 520.0;
        final browserViewport = Container(
          key: ValueKey('webbrowser-viewport-$_reloadSession'),
          height: boundedHeight ? null : fallbackHeight,
          width: double.infinity,
          clipBehavior: Clip.antiAlias,
          decoration: BoxDecoration(
            color: c.surfaceSunk,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.borderInput),
          ),
          child: EmbeddedBrowser(
            key: ValueKey('embedded-wasm-$resolvedUrl-$_reloadSession'),
            uri: resolvedUrl,
            onConnect: null,
            onDisconnect: null,
          ),
        );

        return IcAcrylicBox(
          key: ValueKey('wasm-browser-frame-${widget.url}'),
          padding: const EdgeInsets.all(8),
          borderRadius: BorderRadius.circular(IcRadius.card),
          child: Column(
            mainAxisSize: boundedHeight ? MainAxisSize.max : MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  IcAppIcon(appId: _appId, size: 20),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      widget.title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        13,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
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
                  TagPill(
                    label: 'Session #${_reloadSession + 1}',
                    tone: TagTone.done,
                    icon: LucideIcons.circleCheck,
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
                          width: 26,
                          height: 26,
                          decoration: BoxDecoration(
                            color: c.surfaceSunk,
                            borderRadius: BorderRadius.circular(IcRadius.xs),
                            border: Border.all(color: c.borderHairline),
                          ),
                          child: Icon(
                            LucideIcons.rotateCw,
                            size: 13,
                            color: c.textStrong,
                          ),
                        ),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 6),
              if (boundedHeight)
                Expanded(child: browserViewport)
              else
                browserViewport,
            ],
          ),
        );
      },
    );
  }
}

class _InoSessionNavItem {
  const _InoSessionNavItem({
    required this.key,
    required this.label,
    required this.icon,
    required this.selected,
    this.badge,
    this.onTap,
  });

  final String key;
  final String label;
  final IconData icon;
  final bool selected;
  final String? badge;
  final VoidCallback? onTap;
}

class _InoParsedCard {
  const _InoParsedCard({
    required this.key,
    required this.kind,
    required this.status,
    required this.title,
    required this.isWaiting,
    required this.widget,
    this.sourceUri,
  });

  final String key;
  final String kind;
  final String status;
  final String title;
  final String? sourceUri;
  final bool isWaiting;
  final Widget widget;
}

class _InoParsedTurn {
  const _InoParsedTurn({
    required this.userMessage,
    required this.reply,
    required this.routeHint,
    this.recalledBadge,
  });

  final String userMessage;
  final String reply;
  final String routeHint;
  final String? recalledBadge;

  (String?, String?) get attachedResource {
    final match = RegExp(
      r'Explain and summarize\s+([^\(\n]+?)\s*\((neuron://[^\)\s]+)\)',
      caseSensitive: false,
    ).firstMatch(userMessage);
    if (match != null) {
      return (match.group(1)!.trim(), match.group(2)!.trim());
    }
    final atMatch = RegExp(r'^@([^\s]+)\s+\((neuron://[^\)\s]+)\)')
        .firstMatch(userMessage);
    if (atMatch != null) {
      return (atMatch.group(1)!.trim(), atMatch.group(2)!.trim());
    }
    return (null, null);
  }

  String get cleanUserMessage {
    final idx = userMessage.indexOf('\n\n[File Preview]\n');
    return idx >= 0 ? userMessage.substring(0, idx).trim() : userMessage;
  }

  String? get routedAppId {
    if (routeHint == 'postgres:query' || routeHint == 'chart:qa') {
      return 'data';
    }
    if (routeHint.startsWith('routed:')) {
      final rest = routeHint.substring('routed:'.length);
      final dot = rest.indexOf('.');
      final app = dot > 0 ? rest.substring(0, dot) : rest;
      if (const {
        'markdraw',
        'data',
        'photocraft',
        'filmcraft',
        'settings',
        'files',
      }.contains(app)) {
        return app;
      }
    }
    return null;
  }

  String get routedAppLabel => switch (routedAppId) {
    'markdraw' => 'Markdraw Studio',
    'data' => 'Data Studio',
    'photocraft' => 'PhotoCraft Studio',
    'filmcraft' => 'FilmCraft Studio',
    'settings' => 'Settings',
    'files' => 'Files',
    _ => 'App',
  };
}

class _InoResourceSpec {
  const _InoResourceSpec({
    required this.name,
    required this.uri,
    required this.meta,
    required this.icon,
    this.preview,
  });

  final String name;
  final String uri;
  final String meta;
  final IconData icon;
  final String? preview;
}

class _InoChatDesktopWorkspace extends StatefulWidget {
  const _InoChatDesktopWorkspace({
    required this.activeConversationId,
    required this.sessionTitle,
    required this.statusText,
    required this.isListening,
    required this.connectedDb,
    required this.pinnedSessions,
    required this.todaySessions,
    required this.cards,
    required this.turns,
    required this.markdown,
    required this.onSubmitPrompt,
    required this.onOpenApp,
    this.onNewSession,
    this.onConfirmWaiting,
    this.onDismissWaiting,
  });

  final String activeConversationId;
  final String sessionTitle;
  final String statusText;
  final bool isListening;
  final String? connectedDb;
  final List<_InoSessionNavItem> pinnedSessions;
  final List<_InoSessionNavItem> todaySessions;
  final List<_InoParsedCard> cards;
  final List<_InoParsedTurn> turns;
  final Widget Function(String) markdown;
  final ValueChanged<String> onSubmitPrompt;
  final ValueChanged<String> onOpenApp;
  final VoidCallback? onNewSession;
  final VoidCallback? onConfirmWaiting;
  final VoidCallback? onDismissWaiting;

  @override
  State<_InoChatDesktopWorkspace> createState() =>
      _InoChatDesktopWorkspaceState();
}

class _InoChatDesktopWorkspaceState extends State<_InoChatDesktopWorkspace> {
  final TextEditingController _controller = TextEditingController();
  final FocusNode _focusNode = FocusNode();
  bool _sidebarOpen = true;
  bool _showSlashMenu = false;
  bool _showResourceMenu = false;
  _InoResourceSpec? _attachedResource;
  String? _pendingUserPrompt;
  String? _pendingStageLabel;
  int _turnsAtSubmit = 0;
  int _cardsAtSubmit = 0;
  String _convIdAtSubmit = '';
  Timer? _thinkingTimeout;

  bool get _isBackendThinking =>
      widget.statusText.startsWith('Thinking') ||
      widget.statusText.startsWith('Processing');

  bool get _isThinking => _pendingUserPrompt != null || _isBackendThinking;

  String get _effectiveStatusText =>
      _pendingStageLabel ??
      (_isBackendThinking ? widget.statusText : widget.statusText);

  static String _describeThinkingStage(String prompt) {
    final lower = prompt.toLowerCase();
    if (lower.startsWith('/')) {
      return 'Processing command $prompt…';
    }
    if (lower.contains('chart') ||
        lower.contains('cahrt') ||
        lower.contains('chrat') ||
        lower.contains('plot') ||
        lower.contains('select ') ||
        lower.contains('visualize')) {
      return 'Thinking · Querying data & building chart…';
    }
    if (lower.contains('markdraw') ||
        lower.contains('excalidraw') ||
        lower.contains('diagram') ||
        lower.contains('flowchart') ||
        lower.contains('canvas')) {
      return 'Thinking · Co-drawing on Markdraw canvas…';
    }
    if (lower.contains('photocraft') || lower.contains('background')) {
      return 'Thinking · Dispatching to PhotoCraft Studio…';
    }
    if (lower.contains('filmcraft') || lower.contains('timeline')) {
      return 'Thinking · Dispatching to FilmCraft Studio…';
    }
    if (lower.contains('neuron://') || lower.contains('[file preview]')) {
      return 'Thinking · Inspecting workspace file…';
    }
    return 'Thinking · Processing request…';
  }

  static const List<_InoResourceSpec> _workspaceResources = [
    _InoResourceSpec(
      name: 'Nordlys_MSA_v3.pdf',
      uri: 'neuron://owner/local-disk.files/contracts/nordlys-msa-v3.pdf',
      meta: 'PDF · 420 KB · Contracts',
      icon: LucideIcons.fileText,
      preview: 'Master Services Agreement v3 — Nordlys Enterprise AS.\nRenewal Date: 15 Nov 2026.\nAnnual Contract Value: 480,000 NOK.\nSLA Tier: 99.95% Enterprise Support.',
    ),
    _InoResourceSpec(
      name: 'architecture-notes.md',
      uri: 'neuron://owner/local-disk.files/architecture-notes.md',
      meta: 'Markdown · 4.2 KB · Workspace Disk',
      icon: LucideIcons.fileCode,
      preview: '# IntoChat OS Architecture\n- DigitalBrain Orleans Silo manages stateful neurons.\n- Cross-app handoffs pass canonical neuron:// URIs.\n- LFP cards settle directly in the conversation stream.',
    ),
    _InoResourceSpec(
      name: 'Q4_Pipeline_Opportunities.csv',
      uri: 'neuron://owner/local-disk.files/finance/q4-pipeline.csv',
      meta: 'CSV · 18 KB · Finance',
      icon: LucideIcons.table,
      preview: 'Opportunity,Account,Stage,Amount\nHavbris pilot,Havbris AS,Proposal,€240000\nDepot software phase 2,Acme Logistics,Negotiation,€186000\nFjordline renewal,Fjordline,Contract,€96000',
    ),
    _InoResourceSpec(
      name: 'portrait.png',
      uri: 'neuron://owner/local-disk.files/media/portrait.png',
      meta: 'PNG · 512×512 · PhotoCraft Ready',
      icon: LucideIcons.image,
    ),
  ];

  static const List<(String cmd, String title, String subtitle, IconData icon)>
  _slashCommands = [
    (
      '/scenario q4-renewal',
      'Nordlys Q4 Renewal',
      'Loads waiting ApprovalCard + Email DraftCard',
      LucideIcons.sparkles,
    ),
    (
      '/scenario morning-brief',
      'Morning Brief',
      'Loads daily brief + waiting Circle ChoiceCard',
      LucideIcons.sun,
    ),
    (
      '/scenario invoice-check',
      'Tripletex Invoice Check',
      'Loads zero-leakage Secure Vault SecretCard',
      LucideIcons.keyRound,
    ),
    (
      'SELECT opportunity, account, stage, amount FROM opportunities',
      'Visualize Pipeline SQL',
      'Renders live DataTable + Bar/Pie Chart from Postgres',
      LucideIcons.chartColumn,
    ),
    (
      '/scenario first-run',
      'Ino Setup · First Run',
      'Guided 3-step capability checklist',
      LucideIcons.compass,
    ),
    (
      '/new',
      'New Conversation',
      'Start a clean Ino session',
      LucideIcons.squarePen,
    ),
    (
      '/clear',
      'Clear Session',
      'Clear turns and cards in active session',
      LucideIcons.trash2,
    ),
  ];

  @override
  void didUpdateWidget(_InoChatDesktopWorkspace oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (_pendingUserPrompt != null) {
      final turnAdded = widget.turns.length != _turnsAtSubmit;
      final cardAdded = widget.cards.length != _cardsAtSubmit;
      final convChanged = widget.activeConversationId != _convIdAtSubmit;
      final matchedLastTurn =
          widget.turns.isNotEmpty &&
          widget.turns.last.userMessage == _pendingUserPrompt;
      if (turnAdded || cardAdded || convChanged || matchedLastTurn) {
        _thinkingTimeout?.cancel();
        _pendingUserPrompt = null;
        _pendingStageLabel = null;
      }
    }
  }

  @override
  void dispose() {
    _thinkingTimeout?.cancel();
    _controller.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  void _submitWithThinking(String payload) {
    final trimmed = payload.trim();
    if (trimmed.isEmpty) return;
    _thinkingTimeout?.cancel();
    setState(() {
      if (!trimmed.startsWith('/clear') &&
          !trimmed.startsWith('/new') &&
          !trimmed.startsWith('/pin')) {
        _pendingUserPrompt = trimmed;
        _pendingStageLabel = _describeThinkingStage(trimmed);
        _turnsAtSubmit = widget.turns.length;
        _cardsAtSubmit = widget.cards.length;
        _convIdAtSubmit = widget.activeConversationId;
      } else {
        _pendingUserPrompt = null;
        _pendingStageLabel = null;
      }
    });
    if (_pendingUserPrompt != null) {
      _thinkingTimeout = Timer(const Duration(seconds: 25), () {
        if (mounted && _pendingUserPrompt != null) {
          setState(() {
            _pendingUserPrompt = null;
            _pendingStageLabel = null;
          });
        }
      });
    }
    widget.onSubmitPrompt(trimmed);
  }

  void _sendCurrentText([String? overrideText]) {
    final raw = (overrideText ?? _controller.text).trim();
    final res = _attachedResource;
    if (raw.isEmpty && res == null) return;

    String payload;
    if (res != null) {
      final promptPart = raw.isEmpty
          ? 'Explain and summarize ${res.name} (${res.uri})'
          : 'Explain and summarize ${res.name} (${res.uri}) — $raw';
      payload = res.preview != null && res.preview!.isNotEmpty
          ? '$promptPart\n\n[File Preview]\n${res.preview}'
          : promptPart;
    } else {
      payload = raw;
    }

    setState(() {
      if (overrideText == null) _controller.clear();
      _attachedResource = null;
      _showSlashMenu = false;
      _showResourceMenu = false;
    });
    _submitWithThinking(payload);
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final waitingCard = widget.cards
        .where((card) => card.isWaiting)
        .firstOrNull;

    return LayoutBuilder(
      builder: (context, constraints) {
        final canShowSidebar = constraints.maxWidth >= 680 && _sidebarOpen;
        return Container(
          color: c.surfaceCard,
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (canShowSidebar) _buildArcSidebar(c, waitingCard),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    _buildConversationHeader(
                      c,
                      waitingCard: waitingCard,
                      showSidebarToggle: !canShowSidebar,
                    ),
                    Expanded(
                      child:
                          widget.turns.isEmpty &&
                              widget.cards.isEmpty &&
                              _pendingUserPrompt == null
                          ? _buildEmptyState(c)
                          : _buildConversationStream(c),
                    ),
                    _buildBottomComposer(c, waitingCard),
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }

  // ---------- 1. Left Session Sidebar (248px) ----------

  Widget _buildArcSidebar(IcColors c, _InoParsedCard? waitingCard) {
    final presetIds = const {
      'q4-renewal',
      'morning-brief',
      'invoice-check',
      'first-run',
    };
    final isCustomSession = !presetIds.contains(widget.activeConversationId);

    return Container(
      width: 248,
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: Border(right: BorderSide(color: c.borderHairline)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(10, 10, 10, 8),
            child: Row(
              children: [
                Expanded(
                  child: _NewSessionInlineBtn(onTap: widget.onNewSession),
                ),
                const SizedBox(width: 6),
                _IconBtn(
                  icon: LucideIcons.panelLeftClose,
                  tooltip: 'Hide sidebar',
                  onTap: () => setState(() => _sidebarOpen = false),
                ),
              ],
            ),
          ),

          Expanded(
            child: ListView(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
              children: [
                const _SidebarSectionTitle(label: 'Pinned'),
                for (final item in widget.pinnedSessions)
                  _SidebarSessionRow(item: item, onTap: item.onTap),
                Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 6,
                    vertical: 8,
                  ),
                  child: Divider(height: 1, color: c.borderHairline),
                ),
                _SidebarSectionTitle(
                  label: 'Today',
                  trailingLabel: widget.turns.isNotEmpty ? 'Clear' : null,
                  onTrailingTap: widget.turns.isNotEmpty
                      ? () => widget.onSubmitPrompt('/clear')
                      : null,
                ),
                if (isCustomSession)
                  _SidebarSessionRow(
                    item: _InoSessionNavItem(
                      key: widget.activeConversationId,
                      label: widget.sessionTitle,
                      icon: widget.sessionTitle.startsWith('File ·')
                          ? LucideIcons.fileText
                          : LucideIcons.messageSquare,
                      selected: true,
                      badge: waitingCard != null
                          ? 'Needs you'
                          : (widget.turns.isNotEmpty
                                ? '${widget.turns.length}'
                                : null),
                    ),
                    onTap: () {},
                  ),
                for (final item in widget.todaySessions)
                  _SidebarSessionRow(item: item, onTap: item.onTap),
              ],
            ),
          ),

          // Footer status bar
          Container(
            height: 38,
            padding: const EdgeInsets.symmetric(horizontal: 12),
            decoration: BoxDecoration(
              border: Border(top: BorderSide(color: c.borderHairline)),
            ),
            child: Row(
              children: [
                if (_isThinking)
                  SizedBox(
                    width: 10,
                    height: 10,
                    child: CircularProgressIndicator(
                      strokeWidth: 1.6,
                      color: c.working,
                    ),
                  )
                else
                  Container(
                    width: 7,
                    height: 7,
                    decoration: BoxDecoration(
                      color: widget.isListening ? c.working : c.done,
                      shape: BoxShape.circle,
                    ),
                  ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    _effectiveStatusText,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: IcType.text(
                      11.5,
                      weight: _isThinking ? FontWeight.w600 : FontWeight.w400,
                      color: _isThinking ? c.working : c.textMuted,
                      tnum: true,
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

  // ---------- 2. 52px Conversation Header ----------

  Widget _buildConversationHeader(
    IcColors c, {
    required _InoParsedCard? waitingCard,
    required bool showSidebarToggle,
  }) {
    return Container(
      height: 52,
      padding: const EdgeInsets.symmetric(horizontal: 16),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      child: Row(
        children: [
          if (showSidebarToggle) ...[
            _IconBtn(
              icon: LucideIcons.panelLeft,
              tooltip: 'Show sidebar',
              onTap: () => setState(() => _sidebarOpen = true),
            ),
            const SizedBox(width: 8),
          ],
          Icon(
            widget.sessionTitle.startsWith('File ·')
                ? LucideIcons.fileText
                : LucideIcons.sparkles,
            size: 15,
            color: c.accent,
          ),
          const SizedBox(width: 8),
          Flexible(
            child: Text(
              widget.sessionTitle,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: IcType.text(
                14,
                weight: FontWeight.w600,
                color: c.textStrong,
              ),
            ),
          ),
          if (_isThinking) ...[
            const SizedBox(width: 10),
            TagPill(
              label: _effectiveStatusText,
              tone: TagTone.working,
              icon: LucideIcons.loader,
            ),
          ] else if (waitingCard != null) ...[
            const SizedBox(width: 10),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
              decoration: BoxDecoration(
                color: c.accentTint,
                borderRadius: BorderRadius.circular(IcRadius.sm),
                border: Border.all(color: c.accent.withValues(alpha: 0.35)),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(LucideIcons.hand, size: 12, color: c.accent),
                  const SizedBox(width: 5),
                  Text(
                    'Needs your response',
                    style: IcType.text(
                      11.5,
                      weight: FontWeight.w600,
                      color: c.accent,
                    ),
                  ),
                ],
              ),
            ),
          ],
          if (widget.connectedDb != null) ...[
            const SizedBox(width: 8),
            TagPill(
              label: widget.connectedDb!,
              tone: TagTone.accent,
              icon: LucideIcons.database,
            ),
          ],
          const Spacer(),
          _IconBtn(
            icon: LucideIcons.pin,
            tooltip: 'Pin session',
            onTap: () => widget.onSubmitPrompt('/pin'),
          ),
          const SizedBox(width: 4),
          _IconBtn(
            icon: LucideIcons.plus,
            tooltip: 'New session',
            onTap: widget.onNewSession,
          ),
          const SizedBox(width: 4),
          _IconBtn(
            icon: LucideIcons.slidersHorizontal,
            tooltip: 'AI & Model Settings',
            onTap: () => widget.onOpenApp('settings'),
          ),
        ],
      ),
    );
  }

  // ---------- 3. Empty State ("What can I do for you?") ----------

  Widget _buildEmptyState(IcColors c) {
    final suggestions = [
      (
        title: 'Nordlys Q4 Renewal · Waiting Approval',
        subtitle: 'Review Salesforce close date & Kari Holm email draft',
        prompt: '/scenario q4-renewal',
        icon: LucideIcons.hand,
      ),
      (
        title: 'Visualize Pipeline Opportunities',
        subtitle: 'Query PostgreSQL and render interactive Table + Chart',
        prompt: 'SELECT opportunity, account, stage, amount FROM opportunities',
        icon: LucideIcons.chartColumn,
      ),
      (
        title: 'Ask Ino about Nordlys_MSA_v3.pdf',
        subtitle: 'Attach contract from Files and extract key renewal terms',
        prompt: 'Explain and summarize Nordlys_MSA_v3.pdf (neuron://owner/local-disk.files/contracts/nordlys-msa-v3.pdf)\n\n[File Preview]\nMaster Services Agreement v3 — Nordlys Enterprise AS.\nRenewal Date: 15 Nov 2026.\nAnnual Contract Value: 480,000 NOK.',
        icon: LucideIcons.fileText,
      ),
      (
        title: 'Remove Background in PhotoCraft',
        subtitle: 'Run layer.removeBackground and open WASM Studio',
        prompt: 'Remove background from portrait.png',
        icon: LucideIcons.image,
      ),
    ];

    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 620),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 48,
                height: 48,
                decoration: BoxDecoration(
                  color: c.accentTint,
                  borderRadius: BorderRadius.circular(14),
                  border: Border.all(color: c.accent.withValues(alpha: 0.25)),
                ),
                child: Icon(LucideIcons.sparkles, size: 22, color: c.accent),
              ),
              const SizedBox(height: 16),
              Text(
                'What can I do for you?',
                style: IcType.text(
                  20,
                  weight: FontWeight.w600,
                  color: c.textStrong,
                  letterSpacing: -0.3,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                'Chat with your local model, @attach a file from Files, query Postgres tables & charts, or respond to notifications in chat.',
                textAlign: TextAlign.center,
                style: IcType.text(13, color: c.textMuted, height: 1.4),
              ),
              const SizedBox(height: 22),
              Wrap(
                spacing: 10,
                runSpacing: 10,
                children: [
                  for (final s in suggestions)
                    SizedBox(
                      width: 295,
                      child: _SuggestionPromptCard(
                        icon: s.icon,
                        title: s.title,
                        subtitle: s.subtitle,
                        onTap: () => _submitWithThinking(s.prompt),
                      ),
                    ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  // ---------- 4. Unified Conversation Stream (Messages + Inline Apps & Cards) ----------

  Widget _buildConversationStream(IcColors c) {
    final hasTurns = widget.turns.isNotEmpty;
    final pendingPrompt = _pendingUserPrompt;
    return ListView(
      padding: const EdgeInsets.fromLTRB(24, 18, 24, 20),
      children: [
        Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 760),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: 18,
              children: [
                for (var i = 0; i < widget.turns.length; i++) ...[
                  _buildUserTurnBubble(c, widget.turns[i]),
                  _buildAssistantTurnBlock(
                    c,
                    widget.turns[i],
                    inlineCards: i == widget.turns.length - 1
                        ? widget.cards
                        : const <_InoParsedCard>[],
                  ),
                ],
                if (!hasTurns)
                  for (final card in widget.cards)
                    _buildInlineCardMessage(c, card),
                if (pendingPrompt != null) ...[
                  _buildUserTurnBubble(
                    c,
                    _InoParsedTurn(
                      userMessage: pendingPrompt,
                      reply: '',
                      routeHint: 'working',
                    ),
                  ),
                  _buildThinkingTurnBlock(
                    c,
                    _pendingStageLabel ?? 'Thinking · Processing request…',
                  ),
                ] else if (_isBackendThinking)
                  _buildThinkingTurnBlock(c, widget.statusText),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildThinkingTurnBlock(IcColors c, String stageLabel) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          width: 26,
          height: 26,
          margin: const EdgeInsets.only(top: 2),
          decoration: BoxDecoration(
            color: c.accentTint,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: c.accent.withValues(alpha: 0.35)),
          ),
          alignment: Alignment.center,
          child: SizedBox(
            width: 13,
            height: 13,
            child: CircularProgressIndicator(strokeWidth: 1.8, color: c.accent),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
            decoration: BoxDecoration(
              color: c.surfaceSunk,
              borderRadius: BorderRadius.circular(IcRadius.card),
              border: Border.all(color: c.borderHairline),
            ),
            child: Row(
              children: [
                Icon(LucideIcons.sparkles, size: 14, color: c.working),
                const SizedBox(width: 8),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Ino is thinking…',
                        style: IcType.text(
                          13,
                          weight: FontWeight.w600,
                          color: c.textStrong,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        stageLabel,
                        style: IcType.mono(11.5, color: c.textMuted),
                      ),
                    ],
                  ),
                ),
                const TagPill(label: 'Working', tone: TagTone.working),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildInlineCardMessage(IcColors c, _InoParsedCard card) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          width: 26,
          height: 26,
          margin: const EdgeInsets.only(top: 2),
          decoration: BoxDecoration(
            color: c.accentTint,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: c.accent.withValues(alpha: 0.25)),
          ),
          child: Icon(LucideIcons.sparkles, size: 13, color: c.accent),
        ),
        const SizedBox(width: 12),
        Expanded(child: card.widget),
      ],
    );
  }

  Widget _buildUserTurnBubble(IcColors c, _InoParsedTurn turn) {
    final (resName, resUri) = turn.attachedResource;
    return Align(
      alignment: Alignment.centerRight,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 540),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            if (resName != null && resUri != null) ...[
              Container(
                margin: const EdgeInsets.only(bottom: 6),
                padding: const EdgeInsets.symmetric(
                  horizontal: 10,
                  vertical: 5,
                ),
                decoration: BoxDecoration(
                  color: c.surfaceSunk,
                  borderRadius: BorderRadius.circular(IcRadius.sm),
                  border: Border.all(color: c.borderInput),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(LucideIcons.fileText, size: 13, color: c.accent),
                    const SizedBox(width: 6),
                    Flexible(
                      child: Text(
                        resName,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: IcType.text(
                          12,
                          weight: FontWeight.w600,
                          color: c.textStrong,
                        ),
                      ),
                    ),
                    const SizedBox(width: 6),
                    Flexible(
                      child: Text(
                        resUri,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: IcType.mono(10.5, color: c.textMuted),
                      ),
                    ),
                  ],
                ),
              ),
            ],
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
              decoration: BoxDecoration(
                color: c.accentTint,
                borderRadius: BorderRadius.circular(14),
                border: Border.all(color: c.accent.withValues(alpha: 0.25)),
              ),
              child: SelectableText(
                turn.cleanUserMessage,
                style: IcType.text(13.5, color: c.textStrong, height: 1.45),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildAssistantTurnBlock(
    IcColors c,
    _InoParsedTurn turn, {
    List<_InoParsedCard> inlineCards = const [],
  }) {
    final targetApp = turn.routedAppId;
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          width: 26,
          height: 26,
          margin: const EdgeInsets.only(top: 2),
          decoration: BoxDecoration(
            color: c.accentTint,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: c.accent.withValues(alpha: 0.25)),
          ),
          child: Icon(LucideIcons.sparkles, size: 13, color: c.accent),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              widget.markdown(turn.reply),
              if (inlineCards.isNotEmpty) ...[
                const SizedBox(height: 12),
                for (var i = 0; i < inlineCards.length; i++) ...[
                  if (i > 0) const SizedBox(height: 12),
                  inlineCards[i].widget,
                ],
              ],
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                runSpacing: 6,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  Text(
                    turn.routeHint,
                    style: IcType.mono(11, color: c.textMuted),
                  ),
                  if (turn.recalledBadge != null &&
                      turn.recalledBadge!.isNotEmpty)
                    TagPill(
                      label: turn.recalledBadge!,
                      tone: TagTone.done,
                      icon: LucideIcons.brain,
                    ),
                  GestureDetector(
                    onTap: () {
                      Clipboard.setData(ClipboardData(text: turn.reply));
                      showKitToast(context, 'Copied reply');
                    },
                    child: MouseRegion(
                      cursor: SystemMouseCursors.click,
                      child: Icon(
                        LucideIcons.copy,
                        size: 12,
                        color: c.textMuted,
                      ),
                    ),
                  ),
                  if (targetApp != null)
                    Btn(
                      label: 'Open ${turn.routedAppLabel}',
                      icon: LucideIcons.externalLink,
                      small: true,
                      onTap: () => widget.onOpenApp(targetApp),
                    ),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }

  // ---------- 6. Interactive Bottom Composer (@Resource + /Slash + Input) ----------

  Widget _buildBottomComposer(IcColors c, _InoParsedCard? waitingCard) {
    return Container(
      padding: const EdgeInsets.fromLTRB(20, 8, 20, 14),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 760),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (_showResourceMenu) _buildResourcePickerPopover(c),
              if (_showSlashMenu) _buildSlashPickerPopover(c),
              if (_attachedResource != null) _buildAttachedResourceBar(c),
              Container(
                padding: const EdgeInsets.fromLTRB(10, 6, 8, 6),
                decoration: BoxDecoration(
                  color: c.surfaceSunk,
                  borderRadius: BorderRadius.circular(14),
                  border: Border.all(color: c.borderInput),
                ),
                child: Row(
                  children: [
                    _IconBtn(
                      icon: LucideIcons.atSign,
                      tooltip: 'Attach file or resource (@)',
                      active: _showResourceMenu || _attachedResource != null,
                      onTap: () => setState(() {
                        _showResourceMenu = !_showResourceMenu;
                        _showSlashMenu = false;
                      }),
                    ),
                    const SizedBox(width: 4),
                    _IconBtn(
                      icon: LucideIcons.slash,
                      tooltip: 'Scenarios & commands (/)',
                      active: _showSlashMenu,
                      onTap: () => setState(() {
                        _showSlashMenu = !_showSlashMenu;
                        _showResourceMenu = false;
                      }),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: KeyboardListener(
                        focusNode: FocusNode(),
                        onKeyEvent: (event) {
                          if (event is KeyDownEvent &&
                              event.logicalKey == LogicalKeyboardKey.enter &&
                              !HardwareKeyboard.instance.isShiftPressed) {
                            if (_controller.text.trim().isEmpty &&
                                _attachedResource == null &&
                                waitingCard != null &&
                                widget.onConfirmWaiting != null) {
                              widget.onConfirmWaiting!();
                            }
                          } else if (event is KeyDownEvent &&
                              event.logicalKey == LogicalKeyboardKey.escape &&
                              waitingCard != null &&
                              widget.onDismissWaiting != null) {
                            widget.onDismissWaiting!();
                          }
                        },
                        child: TextField(
                          key: const ValueKey('ino-composer-input'),
                          controller: _controller,
                          focusNode: _focusNode,
                          minLines: 1,
                          maxLines: 4,
                          textInputAction: TextInputAction.send,
                          onSubmitted: (_) => _sendCurrentText(),
                          onChanged: (val) {
                            if (val == '/') {
                              setState(() {
                                _showSlashMenu = true;
                                _showResourceMenu = false;
                              });
                            } else if (val == '@') {
                              setState(() {
                                _showResourceMenu = true;
                                _showSlashMenu = false;
                              });
                            }
                          },
                          style: IcType.text(13.5, color: c.textStrong),
                          decoration: InputDecoration(
                            isDense: true,
                            border: InputBorder.none,
                            hintText: _attachedResource != null
                                ? 'Ask Ino about ${_attachedResource!.name}, or pick a verb above…'
                                : 'Message Ino, @attach a file, or /scenario…',
                            hintStyle: IcType.text(13.5, color: c.textMuted),
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(width: 6),
                    GestureDetector(
                      key: const ValueKey('ino-composer-send'),
                      onTap: _sendCurrentText,
                      child: MouseRegion(
                        cursor: SystemMouseCursors.click,
                        child: Container(
                          width: 30,
                          height: 30,
                          alignment: Alignment.center,
                          decoration: BoxDecoration(
                            color: _isThinking ? c.working : c.accent,
                            borderRadius: BorderRadius.circular(9),
                          ),
                          child: _isThinking
                              ? const SizedBox(
                                  width: 14,
                                  height: 14,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 1.8,
                                    color: Colors.white,
                                  ),
                                )
                              : const Icon(
                                  LucideIcons.arrowUp,
                                  size: 15,
                                  color: Colors.white,
                                ),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildAttachedResourceBar(IcColors c) {
    final res = _attachedResource!;
    final verbs = [
      ('Summarise', 'Summarise key points and action items'),
      ('Explain', 'Explain the structure and context'),
      ('Extract table', 'Extract structured rows as a table'),
      ('Draft reply', 'Draft a stakeholder reply referencing this file'),
    ];
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(10),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        borderRadius: BorderRadius.circular(IcRadius.card),
        border: Border.all(color: c.accent.withValues(alpha: 0.35)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(res.icon, size: 15, color: c.accent),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      res.name,
                      style: IcType.text(
                        12.5,
                        weight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                    Text(
                      '${res.meta} · ${res.uri}',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.mono(11, color: c.textMuted),
                    ),
                  ],
                ),
              ),
              _IconBtn(
                icon: LucideIcons.x,
                tooltip: 'Detach resource',
                onTap: () => setState(() => _attachedResource = null),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final (label, instruction) in verbs)
                Btn(
                  label: label,
                  icon: LucideIcons.sparkles,
                  small: true,
                  onTap: () => _sendCurrentText(instruction),
                ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _buildResourcePickerPopover(IcColors c) {
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(IcRadius.card),
        border: Border.all(color: c.borderInput),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(8, 4, 8, 6),
            child: Row(
              children: [
                Icon(LucideIcons.folderSearch, size: 13, color: c.accent),
                const SizedBox(width: 6),
                Text(
                  'Attach Workspace Resource (neuron://)',
                  style: IcType.text(
                    11.5,
                    weight: FontWeight.w600,
                    color: c.textMuted,
                  ),
                ),
                const Spacer(),
                Btn(
                  label: 'Browse in Files',
                  icon: LucideIcons.externalLink,
                  small: true,
                  onTap: () {
                    setState(() => _showResourceMenu = false);
                    widget.onOpenApp('files');
                  },
                ),
              ],
            ),
          ),
          for (final res in _workspaceResources)
            GestureDetector(
              onTap: () {
                setState(() {
                  _attachedResource = res;
                  _showResourceMenu = false;
                  if (_controller.text == '@') _controller.clear();
                });
                _focusNode.requestFocus();
              },
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 10,
                    vertical: 7,
                  ),
                  decoration: BoxDecoration(
                    borderRadius: BorderRadius.circular(IcRadius.sm),
                  ),
                  child: Row(
                    children: [
                      Icon(res.icon, size: 14, color: c.accent),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          res.name,
                          style: IcType.text(
                            12.5,
                            weight: FontWeight.w600,
                            color: c.textStrong,
                          ),
                        ),
                      ),
                      Text(
                        res.meta,
                        style: IcType.text(11.5, color: c.textMuted),
                      ),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }

  Widget _buildSlashPickerPopover(IcColors c) {
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(IcRadius.card),
        border: Border.all(color: c.borderInput),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(8, 4, 8, 6),
            child: Text(
              'Scenarios & Slash Commands',
              style: IcType.text(
                11.5,
                weight: FontWeight.w600,
                color: c.textMuted,
              ),
            ),
          ),
          for (final (cmd, title, sub, icon) in _slashCommands)
            GestureDetector(
              onTap: () {
                setState(() {
                  _showSlashMenu = false;
                  if (_controller.text == '/') _controller.clear();
                });
                _submitWithThinking(cmd);
              },
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 10,
                    vertical: 6,
                  ),
                  child: Row(
                    children: [
                      Icon(icon, size: 14, color: c.accent),
                      const SizedBox(width: 8),
                      Text(
                        title,
                        style: IcType.text(
                          12.5,
                          weight: FontWeight.w600,
                          color: c.textStrong,
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          sub,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.text(11.5, color: c.textMuted),
                        ),
                      ),
                      Text(cmd, style: IcType.mono(10.5, color: c.textMuted)),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _IconBtn extends StatelessWidget {
  const _IconBtn({
    required this.icon,
    required this.tooltip,
    this.onTap,
    this.active = false,
  });

  final IconData icon;
  final String tooltip;
  final VoidCallback? onTap;
  final bool active;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Tooltip(
      message: tooltip,
      child: GestureDetector(
        onTap: onTap,
        child: MouseRegion(
          cursor: onTap != null
              ? SystemMouseCursors.click
              : SystemMouseCursors.basic,
          child: Container(
            width: 28,
            height: 28,
            decoration: BoxDecoration(
              color: active ? c.accentTint : Colors.transparent,
              borderRadius: BorderRadius.circular(IcRadius.xs),
            ),
            child: Icon(icon, size: 14, color: active ? c.accent : c.textMuted),
          ),
        ),
      ),
    );
  }
}

class _SidebarSectionTitle extends StatelessWidget {
  const _SidebarSectionTitle({
    required this.label,
    this.trailingLabel,
    this.onTrailingTap,
  });

  final String label;
  final String? trailingLabel;
  final VoidCallback? onTrailingTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 4, 8, 4),
      child: Row(
        children: [
          Text(
            label,
            style: IcType.text(11, weight: FontWeight.w600, color: c.textMuted),
          ),
          const Spacer(),
          if (trailingLabel != null && onTrailingTap != null)
            GestureDetector(
              onTap: onTrailingTap,
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Text(
                  trailingLabel!,
                  style: IcType.text(11, color: c.textMuted),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _SidebarSessionRow extends StatelessWidget {
  const _SidebarSessionRow({required this.item, this.onTap});

  final _InoSessionNavItem item;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final isWaitingBadge = item.badge == 'Needs you';
    return GestureDetector(
      onTap: onTap,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: Container(
          height: 32,
          margin: const EdgeInsets.symmetric(vertical: 1),
          padding: const EdgeInsets.symmetric(horizontal: 8),
          decoration: BoxDecoration(
            color: item.selected ? c.surfaceElevated : Colors.transparent,
            borderRadius: BorderRadius.circular(IcRadius.sm),
          ),
          child: Row(
            children: [
              Icon(
                item.icon,
                size: 13.5,
                color: item.selected ? c.accent : c.textMuted,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  item.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    12.5,
                    weight: item.selected ? FontWeight.w600 : FontWeight.w400,
                    color: item.selected ? c.textStrong : c.textBody,
                  ),
                ),
              ),
              if (isWaitingBadge)
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 6,
                    vertical: 2,
                  ),
                  decoration: BoxDecoration(
                    color: c.accentTint,
                    borderRadius: BorderRadius.circular(4),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(LucideIcons.hand, size: 10.5, color: c.accent),
                      const SizedBox(width: 3),
                      Text(
                        'Wait',
                        style: IcType.text(
                          10,
                          weight: FontWeight.w600,
                          color: c.accent,
                        ),
                      ),
                    ],
                  ),
                )
              else if (item.badge != null && item.badge!.isNotEmpty)
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 5,
                    vertical: 1,
                  ),
                  decoration: BoxDecoration(
                    color: c.surfaceCard,
                    borderRadius: BorderRadius.circular(4),
                    border: Border.all(color: c.borderHairline),
                  ),
                  child: Text(
                    item.badge!,
                    style: IcType.text(
                      10,
                      weight: FontWeight.w600,
                      color: c.textMuted,
                      tnum: true,
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _NewSessionInlineBtn extends StatelessWidget {
  const _NewSessionInlineBtn({this.onTap});

  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return GestureDetector(
      onTap: onTap,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: Container(
          height: 30,
          padding: const EdgeInsets.symmetric(horizontal: 8),
          child: Row(
            children: [
              Icon(LucideIcons.plus, size: 13.5, color: c.textMuted),
              const SizedBox(width: 8),
              Text(
                'New session',
                style: IcType.text(
                  12.5,
                  weight: FontWeight.w500,
                  color: c.textMuted,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _SuggestionPromptCard extends StatelessWidget {
  const _SuggestionPromptCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return GestureDetector(
      onTap: onTap,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceSunk,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Container(
                width: 28,
                height: 28,
                decoration: BoxDecoration(
                  color: c.accentTint,
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(icon, size: 14, color: c.accent),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        12.5,
                        weight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                    const SizedBox(height: 3),
                    Text(
                      subtitle,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(11.5, color: c.textMuted, height: 1.3),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _StudioNavRailItem extends StatefulWidget {
  const _StudioNavRailItem({
    required this.label,
    required this.selected,
    this.icon,
    this.badge,
    this.onTap,
  });

  final String label;
  final IconData? icon;
  final bool selected;
  final String? badge;
  final VoidCallback? onTap;

  @override
  State<_StudioNavRailItem> createState() => _StudioNavRailItemState();
}

class _StudioNavRailItemState extends State<_StudioNavRailItem> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return MouseRegion(
      cursor: widget.onTap != null
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 110),
          height: 32,
          padding: const EdgeInsets.symmetric(horizontal: 10),
          decoration: BoxDecoration(
            color: widget.selected
                ? c.accentTint
                : (_hovered ? c.surfaceCard : Colors.transparent),
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(
              color: widget.selected
                  ? c.accent.withValues(alpha: 0.35)
                  : Colors.transparent,
            ),
          ),
          child: Row(
            children: [
              if (widget.icon != null) ...[
                Icon(
                  widget.icon,
                  size: 14,
                  color: widget.selected ? c.accent : c.textMuted,
                ),
                const SizedBox(width: 8),
              ],
              Expanded(
                child: Text(
                  widget.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    12.5,
                    weight: widget.selected ? FontWeight.w600 : FontWeight.w500,
                    color: widget.selected ? c.textStrong : c.textBody,
                  ),
                ),
              ),
              if (widget.badge != null && widget.badge!.isNotEmpty)
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 6,
                    vertical: 1.5,
                  ),
                  decoration: BoxDecoration(
                    color: widget.selected ? c.surfaceCard : c.surfaceSunk,
                    borderRadius: BorderRadius.circular(999),
                    border: Border.all(color: c.borderHairline),
                  ),
                  child: Text(
                    widget.badge!,
                    style: IcType.text(
                      10,
                      weight: FontWeight.w600,
                      color: widget.selected ? c.accent : c.textMuted,
                      tnum: true,
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _InteractiveMarkdrawStudioCard extends StatefulWidget {
  const _InteractiveMarkdrawStudioCard({
    super.key,
    required this.title,
    required this.activeDocument,
    required this.format,
    required this.content,
    required this.revision,
    required this.status,
    this.lastExportPath,
    required this.height,
    required this.onSyncScene,
    required this.onExport,
  });

  final String title;
  final String activeDocument;
  final String format;
  final String content;
  final int revision;
  final String status;
  final String? lastExportPath;
  final double height;
  final void Function(String serializedContent, String documentPath)
  onSyncScene;
  final void Function(String format) onExport;

  @override
  State<_InteractiveMarkdrawStudioCard> createState() =>
      _InteractiveMarkdrawStudioCardState();
}

class _InteractiveMarkdrawStudioCardState
    extends State<_InteractiveMarkdrawStudioCard> {
  late final md.MarkdrawController _controller = md.MarkdrawController(
    config: const md.MarkdrawEditorConfig(initialBackground: '#0f172a'),
  );
  String _lastLoadedContent = '';
  int _lastLoadedRevision = -1;

  @override
  void initState() {
    super.initState();
    _loadScene(widget.content, widget.activeDocument, widget.revision);
  }

  @override
  void didUpdateWidget(_InteractiveMarkdrawStudioCard oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.revision != _lastLoadedRevision ||
        widget.activeDocument != oldWidget.activeDocument ||
        (widget.content != oldWidget.content &&
            widget.content != _lastLoadedContent)) {
      _loadScene(widget.content, widget.activeDocument, widget.revision);
    }
  }

  void _loadScene(String rawContent, String docPath, int rev) {
    if (rawContent.trim().isEmpty) return;
    try {
      final fileName = docPath.endsWith('.excalidraw')
          ? docPath
          : '$docPath.markdraw';
      _controller.loadFromContent(rawContent, fileName);
      _lastLoadedContent = rawContent;
      _lastLoadedRevision = rev;
    } catch (_) {
      // Ignore transient parse errors if partial markdown is being streamed
    }
  }

  void _syncLiveCanvasToBackend() {
    try {
      final docFormat = widget.activeDocument.endsWith('.excalidraw')
          ? md.DocumentFormat.excalidraw
          : md.DocumentFormat.markdraw;
      final serialized = _controller.serializeScene(format: docFormat);
      _lastLoadedContent = serialized;
      widget.onSyncScene(serialized, widget.activeDocument);
      if (mounted) {
        showKitToast(
          context,
          'Synced Markdraw canvas to ${widget.activeDocument}',
        );
      }
    } catch (_) {}
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return IcAcrylicBox(
      padding: const EdgeInsets.all(12),
      borderRadius: BorderRadius.circular(IcRadius.card),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              const IcAppIcon(appId: 'markdraw', size: 24),
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
                        13,
                        weight: FontWeight.w700,
                        color: c.textStrong,
                      ),
                    ),
                    Text(
                      '${widget.activeDocument} · rev ${widget.revision}${widget.lastExportPath != null ? ' · exported ${widget.lastExportPath}' : ''}',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.code(11, color: c.textMuted),
                    ),
                  ],
                ),
              ),
              Btn(
                label: 'Sync Canvas',
                small: true,
                primary: true,
                icon: LucideIcons.refreshCw,
                onTap: _syncLiveCanvasToBackend,
              ),
              const SizedBox(width: 6),
              Btn(
                label: '.excalidraw',
                small: true,
                icon: LucideIcons.fileCode,
                onTap: () {
                  final json = _controller.serializeScene(
                    format: md.DocumentFormat.excalidraw,
                  );
                  Clipboard.setData(ClipboardData(text: json));
                  widget.onExport('excalidraw');
                  showKitToast(
                    context,
                    'Exported .excalidraw to Files & copied JSON',
                  );
                },
              ),
              const SizedBox(width: 6),
              Btn(
                label: '.svg',
                small: true,
                icon: LucideIcons.image,
                onTap: () {
                  final svg = _controller.exportSvg(selectedOnly: false);
                  Clipboard.setData(ClipboardData(text: svg));
                  widget.onExport('svg');
                  showKitToast(context, 'Exported .svg to Files & copied SVG');
                },
              ),
            ],
          ),
          const SizedBox(height: 10),
          SizedBox(
            height: widget.height,
            width: double.infinity,
            child: ClipRRect(
              borderRadius: BorderRadius.circular(IcRadius.sm),
              child: md.MarkdrawEditor(
                controller: _controller,
                onSave: _syncLiveCanvasToBackend,
                onExportSvg: () => widget.onExport('svg'),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
