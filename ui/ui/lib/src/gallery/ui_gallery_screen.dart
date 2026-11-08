import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../components/kit/kit_toast.dart';
import '../components/kit/ui_kit_button.dart';
import '../design/app_tile.dart';
import '../design/brain_scene_3d.dart';
import '../design/cards/cards.dart';
import '../design/controls.dart';
import '../design/copy/ino_copy.dart';
import '../design/tokens.dart';
import '../models/ui_part.dart';
import '../theme/ui_theme.dart';

part 'ui_gallery_previews.dart';

typedef GalleryApp = UiGalleryScreen;
typedef GallerySample = GalleryComponentSpec;

enum GalleryStateKnob { interactive, loading, disabled, empty }

enum GallerySurfaceKnob { acrylic, elevated, recessed }

enum GalleryChartKindKnob { line, bar, sparkline }

@immutable
class GalleryComponentSpec {
  const GalleryComponentSpec({
    required this.id,
    required this.title,
    required this.subtitle,
    required this.category,
    required this.icon,
    required this.contract,
    required this.signal,
  });

  const GalleryComponentSpec.item(
    this.id,
    this.title,
    this.subtitle,
    this.category,
    this.icon,
    this.contract,
    this.signal,
  );

  final String id;
  final String title;
  final String subtitle;
  final String category;
  final IconData icon;
  final String contract;
  final String signal;
}

const _cDesign = 'Design & Identity';
const _cInput = 'Basic Input';
const _cLayout = 'Layout & App Shell';
const _cData = 'Data & 13 LFP Chat Cards';
const _cBrain = '3D Brain & Neurons';

const galleryComponentSpecs = <GalleryComponentSpec>[
  // 1. Design & Identity
  GalleryComponentSpec.item(
    'themes',
    '7 UI Identities & Glass Materiality',
    'Fluent Acrylic, Spatial Glass, Cyber Synaptic, Warm Paper, Warm Mica, Linear Carbon & Nordic Tactical.',
    _cDesign,
    LucideIcons.palette,
    'IntoChat.OS.Identity.IUserSettings',
    'UserSettingsChanged',
  ),
  GalleryComponentSpec.item(
    'typography',
    'Typography (Inter + InterDisplay + tnum)',
    'Inter and InterDisplay scale with strict tabular numerals (tnum) for live telemetry.',
    _cDesign,
    LucideIcons.type,
    'IntoChat.OS.Modules.Flutter.Text.IText',
    'TextChanged',
  ),
  GalleryComponentSpec.item(
    'iconography',
    'Iconography & Squircle IcAppIcon Builder',
    'Two-stop gradient squircle app badges with 1px specular border and custom/Lucide vector glyphs.',
    _cDesign,
    LucideIcons.sparkles,
    'DigitalBrain.AppManifest',
    'AppRegisteredSignal',
  ),
  GalleryComponentSpec.item(
    'acrylic',
    'Acrylic & Elevation Tokens',
    'Translucent multi-layer surfaces with Gaussian blur and 1px specular borders.',
    _cDesign,
    LucideIcons.layers,
    'IntoChat.OS.Modules.Flutter.Surface.ISurface',
    'SurfaceChanged',
  ),
  // 2. Basic Input
  GalleryComponentSpec.item(
    'buttons',
    'Btn (Styles, Sizes & Icons)',
    'Primary, Secondary, Ghost, and Danger buttons in Standard (36px) and Small (30px) sizes.',
    _cInput,
    LucideIcons.mousePointerClick,
    'IntoChat.OS.Modules.Flutter.Button.IButton',
    'ButtonClickedSignal',
  ),
  GalleryComponentSpec.item(
    'inputs',
    'Field & TextField',
    'Desktop-first EditableText fields with neuron:// address validation and instant submit.',
    _cInput,
    LucideIcons.textCursorInput,
    'IntoChat.OS.Modules.Flutter.TextField.ITextField',
    'TextFieldChanged',
  ),
  GalleryComponentSpec.item(
    'switch',
    'IcSwitch (with Lock State)',
    'Instant-save toggle switch with policy lock indicator and keyboard focus ring.',
    _cInput,
    LucideIcons.toggleRight,
    'IntoChat.OS.Modules.Flutter.Switch.ISwitch',
    'SwitchToggledSignal',
  ),
  GalleryComponentSpec.item(
    'select',
    'Select & Dropdown',
    'Single-choice segmented and dropdown selectors backed by DigitalBrain.UI.Select.',
    _cInput,
    LucideIcons.listFilter,
    'IntoChat.OS.Modules.Flutter.Select.ISelect',
    'SelectionChangedSignal',
  ),
  GalleryComponentSpec.item(
    'tag-pill',
    'TagPill & Status Badges',
    'Semantic status pills (Neutral, Accent, Done, Working, Error) paired with vector glyphs.',
    _cInput,
    LucideIcons.tag,
    'IntoChat.OS.Modules.Flutter.Badge.IBadge',
    'StatusToneChanged',
  ),
  GalleryComponentSpec.item(
    'progress-infobar',
    'Progress & InfoBar',
    'Determinate progress bars and contextual status banners with action buttons.',
    _cInput,
    LucideIcons.loader,
    'IntoChat.OS.Modules.Flutter.InfoBar.IInfoBar',
    'InfoBarUpdatedSignal',
  ),
  // 3. Layout & App Shell
  GalleryComponentSpec.item(
    'app-shell',
    'Ui.AppShell (Sidebar + Toolbar + Inspector)',
    'Ergonomic 30-line C# desktop layout builder with Ui.NavItem rail and split inspector.',
    _cLayout,
    LucideIcons.panelLeft,
    'DigitalBrain.UI.AppShellNode',
    'AppShellNavigatedSignal',
  ),
  GalleryComponentSpec.item(
    'tabs',
    'Tabs & Segmented Views',
    'Multi-pane tab strips switching live DigitalBrain.UI view subtrees.',
    _cLayout,
    LucideIcons.folderKanban,
    'IntoChat.OS.Modules.Flutter.Tabs.ITabs',
    'ActiveTabChanged',
  ),
  GalleryComponentSpec.item(
    'surfaces',
    'IcAcrylicBox Surfaces',
    'Acrylic, Elevated, and Recessed containers with adaptive border hairlines.',
    _cLayout,
    LucideIcons.squareStack,
    'IntoChat.OS.Modules.Flutter.Surface.ISurface',
    'SurfaceMutatedSignal',
  ),
  GalleryComponentSpec.item(
    'stat-card',
    'StatCard (Ui.StatCard)',
    'Tabular KPI metric cards with semantic delta TagPill and vector icon.',
    _cLayout,
    LucideIcons.trendingUp,
    'DigitalBrain.UI.StatCardNode',
    'MetricRefreshedSignal',
  ),
  // 4. Data & 13 LFP Chat Cards
  GalleryComponentSpec.item(
    'table-card',
    'TableCard (Live Sort & Filter)',
    'Sortable tabular data grid with tnum numeric alignment and neuron:// source copy.',
    _cData,
    LucideIcons.table,
    'IntoChat.OS.Modules.Flutter.Table.ITable',
    'TableSortedSignal',
  ),
  GalleryComponentSpec.item(
    'chart-card',
    'ChartCard (Line / Bar / Compare)',
    'Comparative series chart with live Line/Columns toggle and 1-click table switch.',
    _cData,
    LucideIcons.chartColumn,
    'IntoChat.OS.Modules.Flutter.Chart.IChart',
    'ChartSeriesUpdated',
  ),
  GalleryComponentSpec.item(
    'approval-card',
    'ApprovalCard',
    'Human-in-the-loop confirmation card with before/after diff and keyboard shortcut.',
    _cData,
    LucideIcons.checkCheck,
    'IntoChat.OS.Confirmation.IConfirmationInbox',
    'ConfirmationRequested',
  ),
  GalleryComponentSpec.item(
    'choice-card',
    'ChoiceCard',
    'Numbered disambiguation options rendered inline in Ino sessions.',
    _cData,
    LucideIcons.listChecks,
    'IntoChat.OS.Assistant.IChoicePrompt',
    'ChoiceSelectedSignal',
  ),
  GalleryComponentSpec.item(
    'draft-card',
    'DraftCard',
    'Inline editable email/Slack/SMS draft with Send, Schedule, and Discard actions.',
    _cData,
    LucideIcons.mail,
    'IntoChat.OS.Assistant.IDraftCard',
    'DraftDispatchedSignal',
  ),
  GalleryComponentSpec.item(
    'change-preview-card',
    'ChangePreviewCard',
    'Field-by-field CRM mutation preview with inline click-to-correct values.',
    _cData,
    LucideIcons.gitCompare,
    'IntoChat.OS.Assistant.IChangePreview',
    'ChangeAppliedSignal',
  ),
  GalleryComponentSpec.item(
    'app-install-card',
    'AppInstallCard',
    'Verified Smart App manifest card with capability review and 1-click install.',
    _cData,
    LucideIcons.packagePlus,
    'IntoChat.OS.Apps.IAppInstaller',
    'AppInstallReviewedSignal',
  ),
  GalleryComponentSpec.item(
    'record-card',
    'RecordCard',
    'Two-column entity snapshot card with overdue warning indicators.',
    _cData,
    LucideIcons.contact,
    'IntoChat.OS.Apps.IRecordView',
    'RecordLoadedSignal',
  ),
  GalleryComponentSpec.item(
    'secret-card',
    'SecretCard',
    'Zero-leakage credential request card routing secrets directly to Vault.',
    _cData,
    LucideIcons.keyRound,
    'IntoChat.OS.Security.ISecretVault',
    'SecretStoredSignal',
  ),
  GalleryComponentSpec.item(
    'job-card',
    'JobCard',
    'Multi-step background workflow progress card with live step status.',
    _cData,
    LucideIcons.workflow,
    'IntoChat.OS.Runtime.IJobRunner',
    'JobStepAdvancedSignal',
  ),
  GalleryComponentSpec.item(
    'remember-card',
    'RememberCard',
    'Episodic memory confirmation card with click-to-edit memory text.',
    _cData,
    LucideIcons.brain,
    'DigitalBrain.Memory.IEpisodicMemory',
    'MemoryCommittedSignal',
  ),
  GalleryComponentSpec.item(
    'file-cards',
    'FileCards (FileCard, FileChip & SaveToPicker)',
    'Artifact write cards, hover path chips, and conflict/folder pickers.',
    _cData,
    LucideIcons.fileCheck,
    'IntoChat.OS.Storage.IFilesWorkspace',
    'FileSavedSignal',
  ),
  GalleryComponentSpec.item(
    'error-line',
    'ErrorLine',
    'Single-line actionable recovery bar for timeouts, rate limits, and auth grants.',
    _cData,
    LucideIcons.triangleAlert,
    'IntoChat.OS.Assistant.ISessionDiagnostics',
    'RecoveryTriggeredSignal',
  ),
  // 5. 3D Brain & Neurons
  GalleryComponentSpec.item(
    'brain-3d',
    'BrainScene3D Playground',
    'Interactive 3D orbital neural graph with node count, orbit speed, pulse trigger & 4 shader modes.',
    _cBrain,
    LucideIcons.brainCircuit,
    'IntoChat.OS.Modules.Flame.Scene.IScene',
    'SynapsePulseSignal',
  ),
  GalleryComponentSpec.item(
    'neuron-paths',
    'Distributed NeuronId Addressing',
    'Canonical neuron:// URIs across Files, UI controls, and cross-brain Circle grants.',
    _cBrain,
    LucideIcons.network,
    'DigitalBrain.NeuronId',
    'SynapseRoutedSignal',
  ),
];

const gallerySamples = galleryComponentSpecs;

/// Single-Format Interactive WinUI 3 Component Studio for `intochat_os_ui`.
final class UiGalleryScreen extends StatefulWidget {
  const UiGalleryScreen({
    super.key,
    this.onButtonPressed,
    this.initialPreset,
    this.initialBarLayout,
    this.onStylePresetChanged,
    this.onBarLayoutChanged,
    this.onDarkModeChanged,
  });

  final ValueChanged<UiButtonPart>? onButtonPressed;
  final IcStylePreset? initialPreset;
  final BarLayout? initialBarLayout;
  final ValueChanged<IcStylePreset>? onStylePresetChanged;
  final ValueChanged<BarLayout>? onBarLayoutChanged;
  final ValueChanged<bool>? onDarkModeChanged;

  @override
  State<UiGalleryScreen> createState() => _UiGalleryScreenState();
}

final class _UiGalleryScreenState extends State<UiGalleryScreen> {
  static const _categories = [
    ('Home', LucideIcons.house),
    (_cDesign, LucideIcons.palette),
    (_cInput, LucideIcons.mousePointerClick),
    (_cLayout, LucideIcons.panelLeft),
    (_cData, LucideIcons.table),
    (_cBrain, LucideIcons.brainCircuit),
  ];

  String _nav = 'Home';
  GalleryComponentSpec? _activeSpec;
  late IcStylePreset _stylePreset =
      widget.initialPreset ?? IcStylePreset.fluentAcrylic;
  bool _isDark = true;

  // Interactive Property Knobs
  UiKitButtonStyle _knobStyle = UiKitButtonStyle.primary;
  TagTone _knobTone = TagTone.accent;
  bool _knobSmall = false;
  GalleryStateKnob _knobState = GalleryStateKnob.interactive;
  GallerySurfaceKnob _knobSurface = GallerySurfaceKnob.acrylic;
  GalleryChartKindKnob _knobChartKind = GalleryChartKindKnob.bar;

  // Component-specific live interactive state
  bool _switchVal = true;
  bool _switchLocked = false;
  String _selectedOption = 'claude-sonnet-4-5';
  String _selectedIconAppId = 'ino';
  String _shellNav = 'overview';
  int _codeTab = 0;
  int _signalPulseCount = 0;
  int _brainNodeCount = 7;
  double _brainOrbitSpeed = 1.0;
  IcBrainShaderMode? _brainShaderOverride;
  String _selectedBrainNode = 'ino';
  ApprovalState _approvalState = ApprovalState.pending;
  int? _choicePicked;
  DraftState _draftState = DraftState.pending;
  ChangeState _changeState = ChangeState.pending;
  AppInstallState _installState = AppInstallState.pending;
  SecretState _secretState = SecretState.pending;
  RememberState _rememberState = RememberState.pending;
  final FileCardState _fileCardState = FileCardState.saved;

  final _search = TextEditingController();
  final _searchFocus = FocusNode();
  final _demoInput = TextEditingController(
    text: 'neuron://nordlys/apps.ino/main',
  );
  final _demoInputFocus = FocusNode();
  late final DraftData _draftData = DraftData(
    channel: DraftChannel.email,
    to: 'Kari Holm <kari@nordlys.no>',
    subject: 'Nordlys Q4 Enterprise Renewal — 15 Nov confirmation',
    body: 'Hi Kari,\nConfirmed: we updated the Q4 renewal close date to 15 Nov 2026 (480,000 NOK).',
    from: 'jeff@intochat.os',
    scheduleLabel: 'tomorrow 08:00',
  );
  late final ChangeData _changeData = ChangeData(
    objectLabel: 'Opportunity',
    name: 'Nordlys Q4 Enterprise',
    systemName: 'Salesforce',
    fields: [
      ChangeField(
        label: 'Close date',
        oldValue: '30 Oct 2026',
        newValue: '15 Nov 2026',
      ),
      ChangeField(
        label: 'Amount',
        oldValue: '450,000 NOK',
        newValue: '480,000 NOK',
      ),
    ],
  );
  late final RememberData _rememberData = RememberData(
    text: 'Always format Nordlys renewal amounts in NOK with tabular figures.',
    kind: 'Nordlys AS',
  );

  @override
  void dispose() {
    _search.dispose();
    _searchFocus.dispose();
    _demoInput.dispose();
    _demoInputFocus.dispose();
    super.dispose();
  }

  List<GalleryComponentSpec> _filteredSpecs() {
    final q = _search.text.trim().toLowerCase();
    return galleryComponentSpecs.where((s) {
      if (_nav != 'Home' && s.category != _nav) return false;
      if (q.isEmpty) return true;
      return '${s.id} ${s.title} ${s.subtitle} ${s.category} ${s.contract} ${s.signal}'
          .toLowerCase()
          .contains(q);
    }).toList();
  }

  String _cap(String s) =>
      s.isEmpty ? s : s[0].toUpperCase() + s.substring(1);

  void _setPreset(IcStylePreset preset) {
    setState(() {
      _stylePreset = preset;
      if (preset == IcStylePreset.warmPaper ||
          preset == IcStylePreset.fluentWarmMica) {
        _isDark = false;
      } else if (!_isDark) {
        _isDark = true;
      }
    });
    widget.onStylePresetChanged?.call(preset);
    widget.onBarLayoutChanged?.call(preset.defaultBarLayout);
  }

  void _setDark(bool dark) {
    setState(() => _isDark = dark);
    widget.onDarkModeChanged?.call(dark);
  }


  @override
  Widget build(BuildContext context) {
    final icColors = IcColors.forPreset(_stylePreset, dark: _isDark);
    final themeData = _isDark
        ? UiTheme.dark(_stylePreset.id)
        : UiTheme.light(_stylePreset.id);

    return Theme(
      data: themeData,
      child: IcTheme(
        colors: icColors,
        child: Material(
          key: const Key('ui_gallery_screen'),
          color: icColors.surfacePage,
          child: LayoutBuilder(
            builder: (context, constraints) {
              final narrow = constraints.maxWidth < 760;
              if (narrow) {
                return _activeSpec != null
                    ? _buildDetailView(
                        context,
                        icColors,
                        _activeSpec!,
                        narrow: true,
                      )
                    : _buildSidebar(context, icColors, fullWidth: true);
              }
              return Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _buildSidebar(context, icColors),
                  Expanded(
                    child: _activeSpec != null
                        ? _buildDetailView(
                            context,
                            icColors,
                            _activeSpec!,
                            narrow: false,
                          )
                        : _buildCatalogGrid(context, icColors),
                  ),
                ],
              );
            },
          ),
        ),
      ),
    );
  }

  Widget _buildSidebar(
    BuildContext context,
    IcColors c, {
    bool fullWidth = false,
  }) {
    final filtered = _filteredSpecs();
    return Container(
      width: fullWidth ? double.infinity : 256,
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: fullWidth
            ? null
            : Border(right: BorderSide(color: c.borderHairline)),
      ),
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              const IcAppIcon(appId: 'gallery', size: 24),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  'UI GALLERY STUDIO',
                  style: IcType.text(
                    11,
                    weight: FontWeight.w700,
                    color: c.textMuted,
                  ),
                ),
              ),
              TagPill(
                label: '${galleryComponentSpecs.length}',
                tone: TagTone.accent,
              ),
            ],
          ),
          const SizedBox(height: 10),
          SizedBox(
            height: 36,
            child: TextField(
              key: const Key('gallery_search'),
              controller: _search,
              focusNode: _searchFocus,
              style: IcType.text(13, color: c.textStrong),
              onChanged: (_) => setState(() {}),
              decoration: InputDecoration(
                hintText: 'Search 5 categories & LFP cards…',
                hintStyle: IcType.text(12, color: c.textMuted),
                prefixIcon: Icon(
                  LucideIcons.search,
                  size: 14,
                  color: c.textMuted,
                ),
                isDense: true,
                contentPadding: const EdgeInsets.symmetric(
                  horizontal: 10,
                  vertical: 8,
                ),
              ),
            ),
          ),
          const SizedBox(height: 8),
          DropdownButtonFormField<String>(
            key: const Key('gallery_category'),
            initialValue: _nav,
            isExpanded: true,
            dropdownColor: c.surfaceCard,
            style: IcType.text(12.5, color: c.textStrong),
            decoration: const InputDecoration(
              isDense: true,
              contentPadding: EdgeInsets.symmetric(horizontal: 10, vertical: 6),
            ),
            items: [
              for (final (cat, _) in _categories)
                DropdownMenuItem(
                  value: cat,
                  child: Text(
                    cat,
                    style: IcType.text(12.5, color: c.textStrong),
                  ),
                ),
            ],
            onChanged: (value) {
              if (value != null) {
                setState(() {
                  _nav = value;
                  _activeSpec = null;
                });
              }
            },
          ),
          const SizedBox(height: 8),
          for (final (cat, icon) in _categories)
            _RailItem(
              label: cat,
              icon: icon,
              selected: _nav == cat && _activeSpec == null,
              onTap: () => setState(() {
                _nav = cat;
                _activeSpec = null;
              }),
            ),
          const Divider(height: 14),
          Expanded(
            child: filtered.isEmpty
                ? Center(
                    child: Text(
                      'No components found',
                      style: IcType.text(13, color: c.textMuted),
                    ),
                  )
                : ListView.builder(
                    itemCount: filtered.length,
                    itemBuilder: (context, index) {
                      final s = filtered[index];
                      return _RailItem(
                        key: Key('gallery_sample_${s.id}'),
                        label: s.title,
                        icon: s.icon,
                        compact: true,
                        selected: _activeSpec?.id == s.id,
                        onTap: () => setState(() => _activeSpec = s),
                      );
                    },
                  ),
          ),
        ],
      ),
    );
  }

  Widget _buildCatalogGrid(BuildContext context, IcColors c) {
    final filtered = _filteredSpecs();
    return ListView(
      padding: const EdgeInsets.fromLTRB(28, 22, 28, 36),
      children: [
        IcAcrylicBox(
          radius: IcRadius.card,
          padding: const EdgeInsets.all(20),
          shadows: IcShadow.md(c),
          child: Row(
            children: [
              const IcAppIcon(appId: 'gallery', size: 44),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'IntoChat UI Gallery · Single-Format Component Studio',
                      style: IcType.heading(22, color: c.textStrong),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      '7 UI Identities · Squircle IcAppIcon · Ergonomic C# DigitalBrain.UI Builders · 13 Interactive LFP Chat Cards · 3D Neural Graph',
                      style: IcType.text(13, color: c.textBody),
                    ),
                  ],
                ),
              ),
              Btn(
                label: c.isDark ? 'Light Mode' : 'Dark Mode',
                small: true,
                icon: c.isDark ? LucideIcons.sun : LucideIcons.moon,
                onTap: () => _setDark(!c.isDark),
              ),
            ],
          ),
        ),
        const SizedBox(height: 14),
        SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: Row(
            children: [
              for (final preset in IcStylePreset.values)
                Padding(
                  padding: const EdgeInsets.only(right: 8),
                  child: Btn(
                    label: preset.label,
                    small: true,
                    primary: _stylePreset == preset,
                    icon: _stylePreset == preset
                        ? LucideIcons.check
                        : LucideIcons.sparkles,
                    onTap: () => _setPreset(preset),
                  ),
                ),
            ],
          ),
        ),
        const SizedBox(height: 20),
        if (filtered.isEmpty)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 48),
            child: Center(
              child: Text(
                'No components found',
                style: IcType.text(14, color: c.textMuted),
              ),
            ),
          )
        else
          Wrap(
            spacing: 14,
            runSpacing: 14,
            children: [
              for (final s in filtered)
                SizedBox(
                  width: 308,
                  child: GestureDetector(
                    onTap: () => setState(() => _activeSpec = s),
                    child: MouseRegion(
                      cursor: SystemMouseCursors.click,
                      child: Container(
                        padding: const EdgeInsets.all(15),
                        decoration: BoxDecoration(
                          color: c.surfaceCard,
                          borderRadius: BorderRadius.circular(IcRadius.card),
                          border: Border.all(color: c.borderHairline),
                        ),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Row(
                              children: [
                                Container(
                                  width: 34,
                                  height: 34,
                                  decoration: BoxDecoration(
                                    color: c.accentTint,
                                    borderRadius: BorderRadius.circular(
                                      IcRadius.sm,
                                    ),
                                  ),
                                  child: Icon(
                                    s.icon,
                                    size: 17,
                                    color: c.accent,
                                  ),
                                ),
                                const SizedBox(width: 10),
                                Expanded(
                                  child: Text(
                                    s.title,
                                    maxLines: 1,
                                    overflow: TextOverflow.ellipsis,
                                    style: IcType.text(
                                      13.5,
                                      weight: FontWeight.w600,
                                      color: c.textStrong,
                                    ),
                                  ),
                                ),
                              ],
                            ),
                            const SizedBox(height: 8),
                            Text(
                              s.subtitle,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: IcType.text(
                                12,
                                color: c.textMuted,
                                height: 1.35,
                              ),
                            ),
                            const SizedBox(height: 10),
                            Wrap(
                              spacing: 6,
                              runSpacing: 4,
                              children: [
                                TagPill(
                                  label: s.category,
                                  tone: TagTone.neutral,
                                ),
                                TagPill(
                                  label: s.signal,
                                  tone: TagTone.accent,
                                  icon: LucideIcons.zap,
                                ),
                              ],
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
            ],
          ),
      ],
    );
  }

  Widget _buildDetailView(
    BuildContext context,
    IcColors c,
    GalleryComponentSpec s, {
    required bool narrow,
  }) {
    final code = switch (_codeTab) {
      0 => _dynamicCSharpCode(s),
      1 => _dynamicFlutterCode(s),
      _ =>
        'Contract: ${s.contract}\nSignal:   [Signal] ${s.signal} (fired $_signalPulseCount times)\nPreset:   ${_stylePreset.id} (${_isDark ? "dark" : "light"}) · Shader: ${_stylePreset.defaultShaderMode.label}',
    };
    final stageBg = switch (_knobSurface) {
      GallerySurfaceKnob.acrylic => c.surfaceGlass,
      GallerySurfaceKnob.elevated => c.surfaceElevated,
      GallerySurfaceKnob.recessed => c.surfaceSunk,
    };

    return SingleChildScrollView(
      key: ValueKey('gallery_detail_${s.id}'),
      padding: EdgeInsets.fromLTRB(narrow ? 16 : 28, 20, narrow ? 16 : 28, 36),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Btn(
                key: const Key('gallery_back'),
                label: 'Back to Gallery',
                small: true,
                icon: LucideIcons.arrowLeft,
                onTap: () => setState(() => _activeSpec = null),
              ),
              TagPill(label: s.category, tone: TagTone.neutral),
              TagPill(
                label: s.contract,
                tone: TagTone.accent,
                icon: LucideIcons.cpu,
              ),
              TagPill(
                label: '[Signal] ${s.signal} ($_signalPulseCount)',
                tone: TagTone.done,
                icon: LucideIcons.zap,
              ),
            ],
          ),
          const SizedBox(height: 14),
          Text(
            s.title,
            style: IcType.heading(
              26,
              weight: FontWeight.w800,
              color: c.textStrong,
            ),
          ),
          const SizedBox(height: 4),
          Text(s.subtitle, style: IcType.text(13.5, color: c.textMuted)),
          const SizedBox(height: 18),
          Container(
            decoration: BoxDecoration(
              color: stageBg,
              borderRadius: BorderRadius.circular(IcRadius.card),
              border: Border.all(
                color: c.isGlass ? c.specularBorder : c.borderHairline,
              ),
              boxShadow: _knobSurface == GallerySurfaceKnob.elevated
                  ? IcShadow.lg(c)
                  : null,
            ),
            child: narrow
                ? Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Padding(
                        padding: const EdgeInsets.all(20),
                        child: _buildStageWidget(context, c, s),
                      ),
                      _buildPropertyKnobsPanel(context, c),
                    ],
                  )
                : Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(
                        child: Padding(
                          padding: const EdgeInsets.all(24),
                          child: _buildStageWidget(context, c, s),
                        ),
                      ),
                      _buildPropertyKnobsPanel(context, c),
                    ],
                  ),
          ),
          const SizedBox(height: 18),
          Container(
            key: const Key('gallery_code_inspector'),
            decoration: BoxDecoration(
              color: c.surfaceCard,
              borderRadius: BorderRadius.circular(IcRadius.card),
              border: Border.all(color: c.borderHairline),
            ),
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Wrap(
                  spacing: 8,
                  runSpacing: 8,
                  alignment: WrapAlignment.spaceBetween,
                  children: [
                    Wrap(
                      spacing: 8,
                      runSpacing: 8,
                      children: [
                        Btn(
                          key: const Key('gallery_tab_csharp'),
                          label: 'C# DigitalBrain.UI',
                          small: true,
                          primary: _codeTab == 0,
                          icon: LucideIcons.code,
                          onTap: () => setState(() => _codeTab = 0),
                        ),
                        Btn(
                          key: const Key('gallery_tab_flutter'),
                          label: 'Flutter Ic*',
                          small: true,
                          primary: _codeTab == 1,
                          icon: LucideIcons.layers,
                          onTap: () => setState(() => _codeTab = 1),
                        ),
                        Btn(
                          key: const Key('gallery_tab_contract'),
                          label: 'Contract & [Signal]',
                          small: true,
                          primary: _codeTab == 2,
                          icon: LucideIcons.radio,
                          onTap: () => setState(() => _codeTab = 2),
                        ),
                      ],
                    ),
                    Wrap(
                      spacing: 8,
                      children: [
                        Btn(
                          key: const Key('gallery_fire_signal'),
                          label: 'Fire Test [Signal]',
                          small: true,
                          primary: true,
                          icon: LucideIcons.zap,
                          onTap: () {
                            setState(() => _signalPulseCount++);
                            showKitToast(
                              context,
                              'Fired [Signal] ${s.signal} (#$_signalPulseCount)',
                            );
                          },
                        ),
                        Btn(
                          key: const Key('gallery_copy_code'),
                          label: 'Copy snippet',
                          small: true,
                          icon: LucideIcons.copy,
                          onTap: () {
                            Clipboard.setData(ClipboardData(text: code));
                            showKitToast(
                              context,
                              'Copied snippet to clipboard.',
                            );
                          },
                        ),
                      ],
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                Container(
                  padding: const EdgeInsets.all(14),
                  decoration: BoxDecoration(
                    color: c.surfaceSunk,
                    borderRadius: BorderRadius.circular(IcRadius.button),
                    border: Border.all(color: c.borderHairline),
                  ),
                  child: SelectableText(
                    code,
                    style: IcType.code(12.5, color: c.textStrong),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildPropertyKnobsPanel(BuildContext context, IcColors c) {
    Widget knobSection<T>(
      String title,
      String prefix,
      List<T> values,
      T current,
      String Function(T) nameOf,
      ValueChanged<T> onPick,
    ) => Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 10, bottom: 5),
          child: Text(
            title,
            style: IcType.text(
              10.5,
              weight: FontWeight.w700,
              color: c.textMuted,
            ),
          ),
        ),
        Wrap(
          spacing: 4,
          runSpacing: 4,
          children: [
            for (final v in values)
              Btn(
                key: Key('${prefix}_${nameOf(v)}'),
                label: _cap(nameOf(v)),
                small: true,
                primary: current == v,
                onTap: () => setState(() => onPick(v)),
              ),
          ],
        ),
      ],
    );

    return Container(
      width: 268,
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: Border(left: BorderSide(color: c.borderHairline)),
        borderRadius: const BorderRadius.horizontal(
          right: Radius.circular(IcRadius.card),
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'PROPERTY KNOBS',
            style: IcType.text(
              11,
              weight: FontWeight.w700,
              color: c.textStrong,
            ),
          ),
          knobSection(
            'STYLE (Primary · Secondary · Ghost · Danger)',
            'knob_style',
            const [
              UiKitButtonStyle.primary,
              UiKitButtonStyle.secondary,
              UiKitButtonStyle.ghost,
              UiKitButtonStyle.danger,
            ],
            _knobStyle,
            (v) => v.name,
            (v) => _knobStyle = v,
          ),
          knobSection(
            'TONE (Neutral · Accent · Done · Working · Error)',
            'knob_tone',
            TagTone.values,
            _knobTone,
            (v) => v.name,
            (v) => _knobTone = v,
          ),
          Padding(
            padding: const EdgeInsets.only(top: 10, bottom: 5),
            child: Text(
              'SIZE (Standard 36px · Small 30px)',
              style: IcType.text(
                10.5,
                weight: FontWeight.w700,
                color: c.textMuted,
              ),
            ),
          ),
          Wrap(
            spacing: 4,
            children: [
              Btn(
                key: const Key('knob_size_standard'),
                label: 'Standard (36px)',
                small: true,
                primary: !_knobSmall,
                onTap: () => setState(() => _knobSmall = false),
              ),
              Btn(
                key: const Key('knob_size_small'),
                label: 'Small (30px)',
                small: true,
                primary: _knobSmall,
                onTap: () => setState(() => _knobSmall = true),
              ),
            ],
          ),
          knobSection(
            'STATE (Interactive · Loading · Disabled · Empty)',
            'knob_state',
            GalleryStateKnob.values,
            _knobState,
            (v) => v.name,
            (v) => _knobState = v,
          ),
          knobSection(
            'SURFACE (Acrylic · Elevated · Recessed)',
            'knob_surface',
            GallerySurfaceKnob.values,
            _knobSurface,
            (v) => v.name,
            (v) => _knobSurface = v,
          ),
          knobSection(
            'CHART KIND (Line · Bar · Sparkline)',
            'knob_chart',
            GalleryChartKindKnob.values,
            _knobChartKind,
            (v) => v.name,
            (v) => _knobChartKind = v,
          ),
          Padding(
            padding: const EdgeInsets.only(top: 10, bottom: 5),
            child: Text(
              'THEME PRESET & DARK/LIGHT',
              style: IcType.text(
                10.5,
                weight: FontWeight.w700,
                color: c.textMuted,
              ),
            ),
          ),
          Row(
            children: [
              Expanded(
                child: DropdownButton<IcStylePreset>(
                  value: _stylePreset,
                  isExpanded: true,
                  dropdownColor: c.surfaceCard,
                  style: IcType.text(12, color: c.textStrong),
                  items: [
                    for (final p in IcStylePreset.values)
                      DropdownMenuItem(value: p, child: Text(p.label)),
                  ],
                  onChanged: (p) {
                    if (p != null) _setPreset(p);
                  },
                ),
              ),
              const SizedBox(width: 8),
              IcSwitch(value: c.isDark, onChanged: _setDark),
            ],
          ),
        ],
      ),
    );
  }

}

class _RailItem extends StatelessWidget {
  const _RailItem({
    super.key,
    required this.label,
    required this.icon,
    required this.selected,
    required this.onTap,
    this.compact = false,
  });

  final String label;
  final IconData icon;
  final bool selected;
  final VoidCallback onTap;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return GestureDetector(
      onTap: onTap,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: Container(
          height: compact ? 30 : 34,
          margin: const EdgeInsets.only(bottom: 2),
          padding: const EdgeInsets.symmetric(horizontal: 10),
          decoration: BoxDecoration(
            color: selected ? c.accentTint : const Color(0x00000000),
            borderRadius: BorderRadius.circular(IcRadius.sm),
          ),
          child: Row(
            children: [
              Icon(
                icon,
                size: compact ? 14 : 15,
                color: selected ? c.accent : c.textMuted,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    compact ? 12 : 13,
                    weight: selected ? FontWeight.w600 : FontWeight.w500,
                    color: selected ? c.textStrong : c.textBody,
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
