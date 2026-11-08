part of '../neuron_view.dart';

extension _UiKitRichCardBuilder on ImmutableView {
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
                      'operationId': ImmutableView._operationId(),
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
          source: ImmutableView._cardSource(context, sourceUri ?? 'Data Table'),
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
          source: ImmutableView._cardSource(
            context,
            subtitle ?? sourceUri ?? 'Chart',
          ),
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
            source: ImmutableView._cardSource(context, sourceApp),
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
          source: ImmutableView._cardSource(context, sourceApp),
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
          source: ImmutableView._cardSource(
            context,
            meta['sourceApp'] as String? ?? 'Ino',
          ),
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
          source: ImmutableView._cardSource(context, channel.noun),
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
          var handle = 'local/app';
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
            source: ImmutableView._cardSource(context, 'IntoChat Hub'),
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
            source: ImmutableView._cardSource(context, 'Episodic Memory'),
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
          var note = 'Workspace file reference';
          for (final f in rawFields) {
            final k = '${f['key'] ?? ''}'.toLowerCase();
            final v = '${f['value'] ?? ''}';
            if (k == 'size') size = v;
            if (k == 'note' || k == 'path') note = v;
          }
          final fileRef = FileRef(
            name: recordTitle,
            path: 'neuron://local/storage.files/$recordTitle',
            icon: LucideIcons.fileCheck,
            size: size,
            where: recordSubtitle ?? 'Workspace Files',
          );
          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            spacing: 10,
            children: [
              FileChip(file: fileRef, onTap: openAction),
              FileCard(
                source: ImmutableView._cardSource(context, 'Files Vault'),
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
          source: ImmutableView._cardSource(context, sourceApp),
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
          source: ImmutableView._cardSource(context, service),
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
          source: ImmutableView._cardSource(context, 'Background Job'),
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
                'operationId': ImmutableView._operationId(),
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
                'operationId': ImmutableView._operationId(),
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
}
