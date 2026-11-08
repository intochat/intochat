part of 'ui_gallery_screen.dart';

extension _UiGalleryPreviews on _UiGalleryScreenState {
  String _dynamicCSharpCode(GalleryComponentSpec s) {
    final styleName = switch (_knobStyle) {
      UiKitButtonStyle.primary => 'ButtonStyle.Primary',
      UiKitButtonStyle.secondary => 'ButtonStyle.Secondary',
      UiKitButtonStyle.ghost || UiKitButtonStyle.link => 'ButtonStyle.Ghost',
      UiKitButtonStyle.danger => 'ButtonStyle.Danger',
    };
    final toneName = switch (_knobTone) {
      TagTone.neutral => 'BadgeTone.Neutral',
      TagTone.accent => 'BadgeTone.Accent',
      TagTone.done => 'BadgeTone.Success',
      TagTone.working => 'BadgeTone.Warning',
      TagTone.error => 'BadgeTone.Danger',
    };
    final enabledLiteral = _knobState != GalleryStateKnob.disabled
        ? 'true'
        : 'false';
    return switch (s.id) {
      'app-shell' =>
        '''Ui.AppShell(
    "studio-shell",
    sidebar: new Column([
        Ui.NavItem("nav-overview", "Overview", "layout-grid", "nav:overview", selected: $_shellNav == "overview"),
        Ui.NavItem("nav-signals", "Signals", "radio", "nav:signals", badge: "${12 + _signalPulseCount}"),
    ]),
    toolbar: new Row([new Heading("${s.title}", Level: 2), new Badge("$_knobTone", Tone: $toneName)]),
    body: Ui.StatCard("kpi-1", "Active Signals/min", "${142 + _signalPulseCount}", "+18.4%", $toneName, "zap")
);''',
      'stat-card' =>
        'Ui.StatCard("kpi-tokens", "Compute Throughput", "1,248,920 tok", delta: "+14.2%", tone: $toneName, icon: "zap");',
      'buttons' =>
        'new Button("Execute Action", new ActionCall("${s.signal}"), Style: $styleName, Compact: $_knobSmall, Enabled: $enabledLiteral, Icon: "sparkles");',
      'chart-card' =>
        'new Chart(Kind: "${_knobChartKind.name}", Title: "Active Neuron Signals per Minute", Points: [new ChartPointNode("ino", "Ino", 48), new ChartPointNode("aspire", "Aspire", 64)], SourceNeuronUri: "neuron://nordlys/microsoft.aspire/telemetry");',
      _ =>
        '''// ${s.contract} · [${s.signal}]
// State: ${_knobState.name} · Surface: ${_knobSurface.name} · Size: ${_knobSmall ? 'Small (30px)' : 'Standard (36px)'}
Ui.AppShell(
    "${s.id}-view",
    sidebar: Ui.NavItem("nav-${s.id}", "${s.title}", "sparkles", "open:${s.id}", selected: true),
    body: new Surface(
        SurfaceKind.${_cap(_knobSurface.name)},
        [
            Ui.StatCard("stat-${s.id}", "${s.title}", "Ready", "${_knobTone.name}", $toneName),
            new Button("Trigger ${s.signal}", new ActionCall("fire:${s.signal}"), Style: $styleName, Enabled: $enabledLiteral),
        ]
    )
);''',
    };
  }

  String _dynamicFlutterCode(GalleryComponentSpec s) {
    final primaryBool = _knobStyle == UiKitButtonStyle.primary;
    final dangerBool = _knobStyle == UiKitButtonStyle.danger;
    final enabledExpr = _knobState == GalleryStateKnob.disabled
        ? 'null'
        : '() => fireSignal("${s.signal}")';
    return switch (s.id) {
      'iconography' =>
        "IcAppIcon(appId: '$_selectedIconAppId', size: ${_knobSmall ? 32 : 48}, accentHex: '${IcAppIcon.resolveSpec(_selectedIconAppId).accentHex}')",
      'buttons' =>
        "Btn(label: '${s.title}', primary: $primaryBool, danger: $dangerBool, small: $_knobSmall, icon: LucideIcons.sparkles, onTap: $enabledExpr)",
      'tag-pill' =>
        "TagPill(label: 'Status · ${_knobTone.name.toUpperCase()}', tone: TagTone.${_knobTone.name}, icon: LucideIcons.sparkles)",
      'brain-3d' =>
        'BrainScene3D(nodes: BrainScene3D.defaultCluster.take($_brainNodeCount).toList(), orbitSpeed: ${_brainOrbitSpeed.toStringAsFixed(1)}, pulseEpoch: $_signalPulseCount, shaderMode: ${_brainShaderOverride ?? _stylePreset.defaultShaderMode})',
      _ =>
        '''// Preset: ${_stylePreset.id} (${_isDark ? 'dark' : 'light'}) · Surface: ${_knobSurface.name}
IcAcrylicBox(
  radius: IcRadius.card,
  padding: const EdgeInsets.all(${_knobSmall ? 14 : 20}),
  child: Column(
    children: [
      TagPill(label: '${s.signal}', tone: TagTone.${_knobTone.name}),
      Btn(label: 'Fire ${s.signal}', primary: $primaryBool, danger: $dangerBool, small: $_knobSmall, onTap: $enabledExpr),
    ],
  ),
)''',
    };
  }

  Widget _buildStageWidget(
    BuildContext context,
    IcColors c,
    GalleryComponentSpec s,
  ) {
    final enabled = _knobState != GalleryStateKnob.disabled;
    if (_knobState == GalleryStateKnob.loading &&
        s.id != 'table-card' &&
        s.id != 'chart-card' &&
        s.id != 'record-card') {
      return Row(
        children: [
          const WorkingSpinner(size: 18),
          const SizedBox(width: 10),
          Text(
            'Loading ${s.title}…',
            style: IcType.text(14, color: c.textBody),
          ),
        ],
      );
    }
    if (_knobState == GalleryStateKnob.empty &&
        s.id != 'table-card' &&
        s.id != 'chart-card') {
      return Text(
        'Empty state for ${s.title}.',
        style: IcType.text(14, color: c.textMuted),
      );
    }

    return switch (s.id) {
      'themes' => Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [
          for (final p in IcStylePreset.values)
            Btn(
              label: '${p.label} (${p.defaultShaderMode.label})',
              small: _knobSmall,
              primary: _stylePreset == p,
              icon: LucideIcons.palette,
              onTap: enabled ? () => _setPreset(p) : null,
            ),
        ],
      ),
      'typography' => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'InterDisplay 32 · 1,492,048 tok',
            style: IcType.heading(
              32,
              weight: FontWeight.w800,
              color: c.textStrong,
            ),
          ),
          const SizedBox(height: 6),
          Text(
            'Inter Body 14 · Tabular numerals keep live telemetry aligned.',
            style: IcType.text(14, color: c.textBody),
          ),
          const SizedBox(height: 6),
          Text(
            'neuron://nordlys/apps.ino/main · 99.98% · 12.4ms',
            style: IcType.code(12.5, color: c.accent),
          ),
        ],
      ),
      'iconography' => Wrap(
        spacing: 12,
        runSpacing: 12,
        children: [
          for (final appId in IcAppIcon.builtInSpecs.keys)
            GestureDetector(
              onTap: () => setState(() => _selectedIconAppId = appId),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  IcAppIcon(appId: appId, size: _knobSmall ? 34 : 46),
                  const SizedBox(height: 4),
                  Text(
                    appId,
                    style: IcType.text(
                      11,
                      weight: _selectedIconAppId == appId
                          ? FontWeight.w700
                          : FontWeight.w500,
                      color: c.textStrong,
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
      'acrylic' || 'surfaces' => IcAcrylicBox(
        radius: IcRadius.card,
        padding: const EdgeInsets.all(20),
        shadows: IcShadow.lg(c),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Surface: ${_knobSurface.name.toUpperCase()} · Sigma ${c.blurSigma.toStringAsFixed(0)}px',
              style: IcType.heading(18, color: c.textStrong),
            ),
            const SizedBox(height: 6),
            Text(
              '1px specular border (${c.preset.label}) with theme-aware elevation.',
              style: IcType.text(13, color: c.textBody),
            ),
          ],
        ),
      ),
      'buttons' => Wrap(
        spacing: 10,
        runSpacing: 10,
        children: [
          UiKitButton(
            label: 'Active Knob (${_knobStyle.name})',
            style: _knobStyle,
            compact: _knobSmall,
            icon: 'sparkles',
            onPressed: enabled
                ? () => showKitToast(context, 'Triggered ${_knobStyle.name}')
                : null,
          ),
          Btn(
            label: 'Primary',
            primary: true,
            small: _knobSmall,
            icon: LucideIcons.rocket,
            onTap: enabled ? () {} : null,
          ),
          Btn(
            label: 'Secondary',
            small: _knobSmall,
            icon: LucideIcons.slidersHorizontal,
            onTap: enabled ? () {} : null,
          ),
          Btn(
            label: 'Danger',
            danger: true,
            small: _knobSmall,
            icon: LucideIcons.trash2,
            onTap: enabled ? () {} : null,
          ),
        ],
      ),
      'inputs' => Container(
        height: _knobSmall ? 30 : 36,
        padding: const EdgeInsets.symmetric(horizontal: 12),
        decoration: BoxDecoration(
          color: c.surfaceSunk,
          borderRadius: BorderRadius.circular(IcRadius.input),
          border: Border.all(color: c.borderInput),
        ),
        alignment: Alignment.centerLeft,
        child: Field(
          controller: _demoInput,
          focusNode: _demoInputFocus,
          placeholder: 'Enter neuron:// address…',
        ),
      ),
      'switch' => Row(
        children: [
          IcSwitch(
            value: _switchVal,
            locked: _switchLocked,
            onChanged: enabled ? (v) => setState(() => _switchVal = v) : null,
          ),
          const SizedBox(width: 12),
          Text(
            _switchVal ? 'Live [Signal] stream active' : 'Stream paused',
            style: IcType.text(13.5, color: c.textStrong),
          ),
          const Spacer(),
          Btn(
            label: _switchLocked ? 'Unlock policy' : 'Lock policy',
            small: true,
            icon: LucideIcons.lock,
            onTap: () => setState(() => _switchLocked = !_switchLocked),
          ),
        ],
      ),
      'select' => Wrap(
        spacing: 8,
        children: [
          for (final model in [
            'claude-sonnet-4-5',
            'gpt-5-thinking',
            'gemini-2.5-pro',
          ])
            Btn(
              label: model,
              small: _knobSmall,
              primary: _selectedOption == model,
              icon: LucideIcons.cpu,
              onTap: enabled
                  ? () => setState(() => _selectedOption = model)
                  : null,
            ),
        ],
      ),
      'tag-pill' => Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [
          for (final tone in TagTone.values)
            TagPill(
              label: '${tone.name.toUpperCase()} BADGE',
              tone: tone,
              icon: LucideIcons.sparkles,
            ),
        ],
      ),
      'progress-infobar' => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          ClipRRect(
            borderRadius: BorderRadius.circular(IcRadius.pill),
            child: LinearProgressIndicator(
              value: 0.72,
              minHeight: 8,
              backgroundColor: c.surfaceSunk,
              color: c.accent,
            ),
          ),
          const SizedBox(height: 12),
          Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: c.accentTint,
              borderRadius: BorderRadius.circular(IcRadius.button),
              border: Border.all(color: c.accent),
            ),
            child: Row(
              children: [
                Icon(LucideIcons.info, size: 16, color: c.accent),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    'Orleans grain pool synced 14/14 [Scenario] gates.',
                    style: IcType.text(13, color: c.textStrong),
                  ),
                ),
                TagPill(label: _knobTone.name, tone: _knobTone),
              ],
            ),
          ),
        ],
      ),
      'app-shell' || 'stat-card' => Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 168,
            child: IcAcrylicBox(
              padding: const EdgeInsets.all(10),
              borderRadius: BorderRadius.circular(IcRadius.card),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Btn(
                    label: 'Overview',
                    small: true,
                    primary: _shellNav == 'overview',
                    icon: LucideIcons.layoutGrid,
                    onTap: () => setState(() => _shellNav = 'overview'),
                  ),
                  const SizedBox(height: 6),
                  Row(
                    children: [
                      Expanded(
                        child: Btn(
                          label: 'Signals',
                          small: true,
                          primary: _shellNav == 'signals',
                          icon: LucideIcons.radio,
                          onTap: () => setState(() => _shellNav = 'signals'),
                        ),
                      ),
                      const SizedBox(width: 4),
                      TagPill(
                        label: '${14 + _signalPulseCount}',
                        tone: TagTone.accent,
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: IcAcrylicBox(
              padding: const EdgeInsets.all(14),
              borderRadius: BorderRadius.circular(IcRadius.card),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      Icon(LucideIcons.zap, size: 14, color: c.textMuted),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(
                          'Active Neuron Signals / min',
                          style: IcType.text(
                            12,
                            weight: FontWeight.w500,
                            color: c.textMuted,
                          ),
                        ),
                      ),
                      TagPill(label: '+18.4%', tone: _knobTone),
                    ],
                  ),
                  const SizedBox(height: 8),
                  Text(
                    '${142 + _signalPulseCount}',
                    style: IcType.text(
                      22,
                      weight: FontWeight.w700,
                      color: c.textStrong,
                      tnum: true,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
      'tabs' => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: 6,
            children: [
              for (final tab in ['overview', 'telemetry', 'contracts'])
                Btn(
                  label: tab.toUpperCase(),
                  small: true,
                  primary: _shellNav == tab,
                  onTap: () => setState(() => _shellNav = tab),
                ),
            ],
          ),
          const SizedBox(height: 12),
          Text(
            'Active tab pane: $_shellNav (tnum 1,024 signals/s)',
            style: IcType.text(13.5, color: c.textStrong, tnum: true),
          ),
        ],
      ),
      'table-card' => TableCard(
        source: CardSource(
          'Salesforce',
          Icon(LucideIcons.cloud, size: 14, color: c.accent),
        ),
        state: _knobState == GalleryStateKnob.loading
            ? TableState.loading
            : (_knobState == GalleryStateKnob.empty
                  ? TableState.empty
                  : TableState.ready),
        data: const TableData(
          title: 'Q4 Renewal Pipeline',
          from: 'Salesforce',
          sourceNeuronUri: 'neuron://nordlys/salesforce.crm/q4-pipeline',
          columns: [
            TableColumn('Account', flex: 4),
            TableColumn('Close Date', date: true, flex: 3),
            TableColumn('Amount (NOK)', numeric: true, flex: 3),
          ],
          rows: [
            ['Nordlys Enterprise', '15 Nov', '480,000'],
            ['Kystverket Cloud', '02 Dec', '920,000'],
            ['Bergen Maritime', '19 Oct', '310,000'],
          ],
        ),
      ),
      'chart-card' => ChartCard(
        source: CardSource(
          'Aspire Telemetry',
          Icon(LucideIcons.activity, size: 14, color: c.accent),
        ),
        state: _knobState == GalleryStateKnob.loading
            ? ChartState.loading
            : (_knobState == GalleryStateKnob.empty
                  ? ChartState.empty
                  : ChartState.ready),
        kind: _knobChartKind == GalleryChartKindKnob.bar
            ? ChartKind.columns
            : ChartKind.line,
        onSwitchChartKind: (k) => setState(
          () => _knobChartKind = k == ChartKind.columns
              ? GalleryChartKindKnob.bar
              : GalleryChartKindKnob.line,
        ),
        data: const ChartData(
          title: 'Active Neuron Signals per Minute',
          from: 'neuron://nordlys/microsoft.aspire/telemetry',
          sourceNeuronUri: 'neuron://nordlys/microsoft.aspire/telemetry',
          points: [
            ChartPoint('Ino', 48),
            ChartPoint('Aspire', 64),
            ChartPoint('CRM', 31),
            ChartPoint('Files', 52),
          ],
          compare: [36, 50, 28, 41],
          seriesName: 'Now',
          compareName: '1h avg',
        ),
      ),
      'approval-card' => ApprovalCard(
        source: CardSource(
          'Salesforce',
          Icon(LucideIcons.cloud, size: 14, color: c.accent),
        ),
        state: _approvalState,
        onApprove: () =>
            setState(() => _approvalState = ApprovalState.approved),
        onReject: () => setState(() => _approvalState = ApprovalState.rejected),
        data: ApprovalData(
          title: 'Update close date on Nordlys Q4 renewal to 15 Nov 2026?',
          summary: "Ino matched Kari Holm's email to your open Salesforce opportunity.",
          valueLabel: 'Close date',
          rows: [
            ApprovalRow(
              label: 'Nordlys Q4 Enterprise',
              amount: '480,000 NOK',
              oldValue: '30 Oct 2026',
              newValue: '15 Nov 2026',
            ),
          ],
        ),
      ),
      'choice-card' => ChoiceCard(
        source: CardSource(
          'Ino Coordinator',
          Icon(LucideIcons.sparkles, size: 14, color: c.accent),
        ),
        question:
            'Which Nordlys workspace should receive the Q4 renewal brief?',
        picked: _choicePicked,
        onPick: (i) => setState(() => _choicePicked = i),
        options: const [
          ChoiceOption(
            'Nordlys Enterprise (Primary)',
            detail: 'neuron://nordlys/circles/enterprise',
          ),
          ChoiceOption(
            'Executive Board Circle',
            detail: 'neuron://nordlys/circles/board',
          ),
        ],
      ),
      'draft-card' => DraftCard(
        source: CardSource(
          'Gmail',
          Icon(LucideIcons.mail, size: 14, color: c.accent),
        ),
        data: _draftData,
        state: _draftState,
        onSend: () => setState(() => _draftState = DraftState.sent),
        onSchedule: () => setState(() => _draftState = DraftState.scheduled),
        onDiscard: () => setState(() => _draftState = DraftState.discarded),
      ),
      'change-preview-card' => ChangePreviewCard(
        source: CardSource(
          'Salesforce',
          Icon(LucideIcons.cloud, size: 14, color: c.accent),
        ),
        data: _changeData,
        state: _changeState,
        onApply: () => setState(() => _changeState = ChangeState.applied),
        onCancel: () => setState(() => _changeState = ChangeState.cancelled),
      ),
      'app-install-card' => AppInstallCard(
        source: CardSource(
          'IntoChat Hub',
          Icon(LucideIcons.packagePlus, size: 14, color: c.accent),
        ),
        state: _installState,
        onReview: () =>
            setState(() => _installState = AppInstallState.installed),
        onDismiss: () =>
            setState(() => _installState = AppInstallState.dismissed),
        data: const AppInstallData(
          name: 'Tripletex Accounting Sync',
          handle: 'nordlys/tripletex-sync',
          publisher: 'Nordlys Digital',
          version: 'v1.4.0',
          summary: 'Synchronizes Norwegian Tripletex invoices with DigitalBrain Files & CRM.',
          icon: IcAppIcon(appId: 'datavisualizer', size: 44),
        ),
      ),
      'record-card' => RecordCard(
        source: CardSource(
          'Salesforce',
          Icon(LucideIcons.cloud, size: 14, color: c.accent),
        ),
        state: _knobState == GalleryStateKnob.loading
            ? RecordState.loading
            : RecordState.loaded,
        data: const RecordData(
          objectLabel: 'Opportunity',
          name: 'Nordlys Q4 Enterprise Renewal',
          subtitle: 'Kari Holm · Oslo, Norway',
          fields: [
            RecordField('Amount', '480,000 NOK'),
            RecordField('Close Date', '15 Nov 2026'),
            RecordField('Stage', 'Negotiation / Review'),
            RecordField('Last Contact', '14 days ago', warn: true),
          ],
        ),
      ),
      'secret-card' => SecretCard(
        source: CardSource(
          'Security Vault',
          Icon(LucideIcons.keyRound, size: 14, color: c.accent),
        ),
        state: _secretState,
        onEnter: () => setState(() => _secretState = SecretState.saved),
        onSkip: () => setState(() => _secretState = SecretState.skipped),
        data: const SecretRequestData(
          name: 'Tripletex Session Token',
          why: 'Required to verify Q4 invoice ledger balances in Tripletex.',
        ),
      ),
      'job-card' => JobCard(
        source: CardSource(
          'C# Sandbox Runner',
          Icon(LucideIcons.workflow, size: 14, color: c.accent),
        ),
        state: JobState.running,
        data: JobData(
          title: 'Verifying BDD [Scenario] Gate & Deploying Smart App',
          progress: 0.75,
          steps: [
            JobStep(
              'Compile C# App<TState> grain',
              detail: '118ms',
              status: StepStatus.done,
            ),
            JobStep(
              'Execute [Scenario] BDD assertions',
              detail: '14/14 green',
              status: StepStatus.done,
            ),
            JobStep(
              'Publish neuron://local/apps/crm',
              detail: 'Streaming…',
              status: StepStatus.running,
            ),
          ],
        ),
      ),
      'remember-card' => RememberCard(
        source: CardSource(
          'Episodic Memory',
          Icon(LucideIcons.brain, size: 14, color: c.accent),
        ),
        data: _rememberData,
        state: _rememberState,
        onSave: () => setState(() => _rememberState = RememberState.saved),
        onSkip: () => setState(() => _rememberState = RememberState.skipped),
      ),
      'file-cards' => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Wrap(
            spacing: 8,
            children: [
              FileChip(
                file: FileRef(
                  name: 'context.md',
                  path: 'neuron://nordlys/storage.files/30-projects/nordlys-as/context.md',
                  icon: LucideIcons.fileText,
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          FileCard(
            source: CardSource(
              'Files Vault',
              Icon(LucideIcons.folder, size: 14, color: c.accent),
            ),
            state: _fileCardState,
            onOpen: () => showKitToast(context, 'Opened context.md'),
            onReveal: () => showKitToast(context, 'Revealed in Files'),
            data: const FileCardData(
              file: FileRef(
                name: 'nordlys-q4-renewal.md',
                path: 'neuron://nordlys/storage.files/30-projects/nordlys-q4-renewal.md',
                icon: LucideIcons.fileCheck,
                size: '18 KB',
                where: '30 Projects › Nordlys AS',
              ),
              note: 'Updated close date to 15 Nov 2026 and attached ledger table.',
            ),
          ),
        ],
      ),
      'error-line' => ErrorLine(
        error: inoErrors.first,
        onAction: () => showKitToast(context, 'Retrying model request…'),
      ),
      'brain-3d' => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final mode in IcBrainShaderMode.values)
                Btn(
                  label: mode.label,
                  small: true,
                  primary:
                      (_brainShaderOverride ?? c.preset.defaultShaderMode) ==
                      mode,
                  onTap: () => setState(() => _brainShaderOverride = mode),
                ),
              Btn(
                label: 'Nodes: $_brainNodeCount',
                small: true,
                icon: LucideIcons.circleDot,
                onTap: () => setState(
                  () => _brainNodeCount = _brainNodeCount >= 7
                      ? 4
                      : _brainNodeCount + 1,
                ),
              ),
              Btn(
                label: 'Speed: ${_brainOrbitSpeed.toStringAsFixed(1)}x',
                small: true,
                icon: LucideIcons.gauge,
                onTap: () => setState(
                  () => _brainOrbitSpeed = _brainOrbitSpeed >= 2.0
                      ? 0.5
                      : _brainOrbitSpeed + 0.5,
                ),
              ),
              Btn(
                label: 'Pulse Signal',
                small: true,
                primary: true,
                icon: LucideIcons.zap,
                onTap: () => setState(() => _signalPulseCount++),
              ),
            ],
          ),
          const SizedBox(height: 12),
          SizedBox(
            height: 280,
            child: ClipRRect(
              borderRadius: BorderRadius.circular(IcRadius.button),
              child: BrainScene3D(
                nodes: BrainScene3D.defaultCluster
                    .take(_brainNodeCount)
                    .toList(),
                synapses: BrainScene3D.defaultSynapses,
                selectedId: _selectedBrainNode,
                orbitSpeed: _brainOrbitSpeed,
                pulseEpoch: _signalPulseCount,
                shaderMode: _brainShaderOverride,
                onSelectNode: (n) => setState(() => _selectedBrainNode = n.id),
              ),
            ),
          ),
        ],
      ),
      'neuron-paths' => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (final uri in [
            'neuron://nordlys/storage.files/30-projects/nordlys-as/context.md',
            'neuron://nordlys/apps/ino/table',
            'neuron://nordlys/runtime/sandbox/csharp',
          ])
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Container(
                padding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 9,
                ),
                decoration: BoxDecoration(
                  color: c.surfaceSunk,
                  borderRadius: BorderRadius.circular(IcRadius.button),
                  border: Border.all(color: c.borderHairline),
                ),
                child: Row(
                  children: [
                    Icon(LucideIcons.network, size: 15, color: c.accent),
                    const SizedBox(width: 10),
                    Expanded(
                      child: Text(
                        uri,
                        style: IcType.code(12, color: c.textStrong),
                      ),
                    ),
                    Btn(
                      label: 'Copy URI',
                      small: true,
                      icon: LucideIcons.copy,
                      onTap: () {
                        Clipboard.setData(ClipboardData(text: uri));
                        showKitToast(context, 'Copied $uri');
                      },
                    ),
                  ],
                ),
              ),
            ),
        ],
      ),
      _ => const SizedBox.shrink(),
    };
  }
}
