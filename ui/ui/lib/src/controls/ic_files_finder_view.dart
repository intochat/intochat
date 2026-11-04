import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:gpt_markdown/gpt_markdown.dart';
import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';
import 'package:url_launcher/url_launcher.dart';

import '../components/image/ui_image.dart';
import '../design/controls.dart';
import '../design/tokens.dart';
import 'ic_breadcrumb_bar.dart';

/// Parsed storage location row from `files-side`.
@immutable
class IcFilesStorageEntry {
  const IcFilesStorageEntry({
    required this.key,
    required this.label,
    required this.quotaText,
    required this.icon,
    required this.selected,
    required this.unavailable,
    this.onTap,
  });

  final String key;
  final String label;
  final String quotaText;
  final IconData icon;
  final bool selected;
  final bool unavailable;
  final VoidCallback? onTap;
}

/// Parsed file or folder row from `items` in `files-main`.
@immutable
class IcFilesItemEntry {
  const IcFilesItemEntry({
    required this.key,
    required this.name,
    required this.isFolder,
    required this.selected,
    required this.kindLabel,
    required this.sizeLabel,
    required this.modifiedLabel,
    required this.whereLabel,
    this.groupLabel,
    this.onOpen,
    this.onSelect,
  });

  final String key;
  final String name;
  final bool isFolder;
  final bool selected;
  final String kindLabel;
  final String sizeLabel;
  final String modifiedLabel;
  final String whereLabel;
  final String? groupLabel;
  final VoidCallback? onOpen;
  final VoidCallback? onSelect;
}

/// Finder-grade Desktop `Files` view matching `E:\intochat\design\apps\os\lib\apps\files\files_app.dart`.
class IcFilesFinderView extends StatefulWidget {
  const IcFilesFinderView({
    super.key,
    required this.storages,
    required this.breadcrumbs,
    required this.items,
    this.activeNeuronUri,
    this.activeDisplayPath,
    this.selectedName,
    this.selectedMeta,
    this.selectedNeuronUri,
    this.inoHandoffPrompt,
    this.previewText,
    this.previewNote,
    this.previewImageWidget,
    this.previewAppCard,
    this.previewUrl,
    this.previewKind,
    this.statusTitle,
    this.statusBody,
    this.noticeText,
    this.isLoading = false,
    this.isEmpty = false,
    this.noStorageConnected = false,
    this.activeSort = 'name',
    this.pageInfoText,
    this.onUp,
    this.onBackFolder,
    this.onSearchAll,
    this.onApplyFilter,
    this.onClearFilter,
    this.onRetry,
    this.onOpenSettings,
    this.onConnectGoogleDrive,
    this.onSortName,
    this.onSortSize,
    this.onSortModified,
    this.onUpload,
    this.onNewFolder,
    this.onRename,
    this.onMove,
    this.onCopy,
    this.onPaste,
    this.onDelete,
    this.onDownload,
    this.onAskIno,
    this.onWriteFile,
    this.onPrevPage,
    this.onNextPage,
    this.onLoadMore,
    this.searchFieldWidget,
    this.filterFieldWidget,
    this.nameFieldWidget,
    this.contentFieldWidget,
    this.confirmationsWidget,
    this.operationWidgets = const [],
    this.trailingBreadcrumbWidgets = const [],
  });

  final List<IcFilesStorageEntry> storages;
  final List<IcBreadcrumbItem> breadcrumbs;
  final List<IcFilesItemEntry> items;

  final String? activeNeuronUri;
  final String? activeDisplayPath;
  final String? selectedName;
  final String? selectedMeta;
  final String? selectedNeuronUri;
  final String? inoHandoffPrompt;
  final String? previewText;
  final String? previewNote;
  final Widget? previewImageWidget;
  final Widget? previewAppCard;
  final String? previewUrl;
  final String? previewKind;
  final String? statusTitle;
  final String? statusBody;
  final String? noticeText;

  final bool isLoading;
  final bool isEmpty;
  final bool noStorageConnected;
  final String activeSort;
  final String? pageInfoText;

  final VoidCallback? onUp;
  final VoidCallback? onBackFolder;
  final VoidCallback? onSearchAll;
  final VoidCallback? onApplyFilter;
  final VoidCallback? onClearFilter;
  final VoidCallback? onRetry;
  final VoidCallback? onOpenSettings;
  final VoidCallback? onConnectGoogleDrive;
  final VoidCallback? onSortName;
  final VoidCallback? onSortSize;
  final VoidCallback? onSortModified;
  final VoidCallback? onUpload;
  final VoidCallback? onNewFolder;
  final VoidCallback? onRename;
  final VoidCallback? onMove;
  final VoidCallback? onCopy;
  final VoidCallback? onPaste;
  final VoidCallback? onDelete;
  final VoidCallback? onDownload;
  final VoidCallback? onAskIno;
  final VoidCallback? onWriteFile;
  final VoidCallback? onPrevPage;
  final VoidCallback? onNextPage;
  final VoidCallback? onLoadMore;

  final Widget? searchFieldWidget;
  final Widget? filterFieldWidget;
  final Widget? nameFieldWidget;
  final Widget? contentFieldWidget;
  final Widget? confirmationsWidget;
  final List<Widget> operationWidgets;
  final List<Widget> trailingBreadcrumbWidgets;

  @override
  State<IcFilesFinderView> createState() => _IcFilesFinderViewState();
}

class _IcFilesFinderViewState extends State<IcFilesFinderView> {
  bool _gridView = false;
  bool _showInspector = true;
  bool _showSearchDrawer = false;
  bool _showAuthorDrawer = false;
  String _authorMode = 'mkdir'; // 'mkdir' | 'write' | 'rename'
  final Set<String> _favouriteFolders = {'Projects', 'Documents', 'Downloads'};

  bool get _hasSelection =>
      widget.selectedName != null || widget.items.any((i) => i.selected);

  IcFilesStorageEntry? get _activeStorage {
    for (final s in widget.storages) {
      if (s.selected) return s;
    }
    return widget.storages.isNotEmpty ? widget.storages.first : null;
  }

  void _copyToClipboard(String text, String label) {
    Clipboard.setData(ClipboardData(text: text));
    ScaffoldMessenger.maybeOf(context)?.showSnackBar(
      SnackBar(
        content: Text('Copied $label: $text'),
        duration: const Duration(seconds: 2),
        behavior: SnackBarBehavior.floating,
      ),
    );
  }

  void _openAuthorDrawer(String mode) {
    setState(() {
      if (_showAuthorDrawer && _authorMode == mode) {
        _showAuthorDrawer = false;
      } else {
        _showAuthorDrawer = true;
        _authorMode = mode;
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return LayoutBuilder(
      builder: (context, constraints) {
        final isNarrow = constraints.maxWidth < 720;
        final showQuickView =
            _showInspector &&
            !isNarrow &&
            (_hasSelection ||
                widget.previewText != null ||
                widget.previewImageWidget != null ||
                widget.previewAppCard != null ||
                widget.previewUrl != null);

        return ColoredBox(
          color: c.surfacePage,
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (!isNarrow) _buildSidebar(c),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    _buildToolbar(c, isNarrow: isNarrow),
                    if (_showSearchDrawer) _buildSearchScopeBar(c),
                    if (_showAuthorDrawer) _buildAuthoringBar(c),
                    if (widget.noticeText != null &&
                        widget.noticeText!.isNotEmpty)
                      _buildNoticeBanner(c, widget.noticeText!),
                    Expanded(
                      child: Row(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          Expanded(child: _buildMainContent(c)),
                          if (showQuickView) _buildQuickViewPane(c),
                        ],
                      ),
                    ),
                    _buildStatusBar(c),
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }

  // ---------- 1. Left Finder Sidebar (216px) ----------

  Widget _buildSidebar(IcColors c) {
    Widget sectionLabel(String text, {Widget? trailing}) => Padding(
      padding: const EdgeInsets.fromLTRB(10, 14, 6, 4),
      child: Row(
        children: [
          Expanded(
            child: Text(
              text.toUpperCase(),
              style: IcType.text(
                11,
                weight: FontWeight.w700,
                color: c.textMuted,
              ).copyWith(letterSpacing: 0.6),
            ),
          ),
          ?trailing,
        ],
      ),
    );

    final folderCrumbs = widget.breadcrumbs
        .where((b) => b.id != 'crumb-root')
        .toList();

    return Container(
      width: 216,
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: Border(right: BorderSide(color: c.borderHairline)),
      ),
      child: ListView(
        padding: const EdgeInsets.fromLTRB(8, 6, 8, 12),
        children: [
          sectionLabel('Favourites'),
          _FinderSideRow(
            icon: LucideIcons.compass,
            label: 'All Storages',
            selected:
                widget.breadcrumbs.length <= 1 && !widget.noStorageConnected,
            onTap: widget.breadcrumbs.isNotEmpty
                ? widget.breadcrumbs.first.onTap
                : null,
          ),
          for (final fav in _favouriteFolders)
            _FinderSideRow(
              icon: LucideIcons.star,
              label: fav,
              selected: false,
              onTap: () {
                for (final item in widget.items) {
                  if (item.isFolder &&
                      item.name.toLowerCase() == fav.toLowerCase()) {
                    item.onOpen?.call();
                    return;
                  }
                }
              },
            ),
          if (folderCrumbs.isNotEmpty) ...[
            sectionLabel(_activeStorage?.label ?? 'Current Path'),
            for (var i = 0; i < folderCrumbs.length; i++)
              _FinderSideRow(
                icon: i == 0 ? LucideIcons.hardDrive : LucideIcons.folder,
                label: folderCrumbs[i].label,
                indent: i * 12.0,
                selected: i == folderCrumbs.length - 1,
                onTap: folderCrumbs[i].onTap,
              ),
          ],
          sectionLabel(
            'Locations',
            trailing: widget.onOpenSettings != null
                ? GestureDetector(
                    onTap: widget.onOpenSettings,
                    child: MouseRegion(
                      cursor: SystemMouseCursors.click,
                      child: Tooltip(
                        message: 'Manage Storage Connections',
                        child: Icon(
                          LucideIcons.plus,
                          size: 13,
                          color: c.textMuted,
                        ),
                      ),
                    ),
                  )
                : null,
          ),
          if (widget.storages.isEmpty)
            Padding(
              padding: const EdgeInsets.fromLTRB(10, 4, 8, 6),
              child: Text(
                'No storages connected.',
                style: IcType.text(12, color: c.textMuted),
              ),
            ),
          for (final s in widget.storages)
            _FinderSideRow(
              itemKey: ValueKey('finder-side:${s.key}'),
              icon: s.icon,
              label: s.label,
              detail: s.quotaText,
              selected: s.selected,
              dim: s.unavailable,
              onTap: s.onTap,
            ),
          if (!widget.storages.any(
            (s) =>
                s.label.toLowerCase().contains('drive') ||
                s.icon == LucideIcons.cloud,
          ))
            _FinderSideRow(
              itemKey: const ValueKey('finder-side:google-drive-connect'),
              icon: LucideIcons.cloud,
              label: 'Google Drive',
              detail: 'Connect Google',
              selected: false,
              dim: true,
              onTap: widget.onConnectGoogleDrive ?? widget.onOpenSettings,
            ),
          if (widget.onOpenSettings != null)
            _FinderSideRow(
              icon: LucideIcons.plug,
              label: 'Storage Settings…',
              selected: false,
              onTap: widget.onOpenSettings,
            ),
          if (widget.operationWidgets.isNotEmpty ||
              widget.confirmationsWidget != null) ...[
            sectionLabel('Activity & Gate'),
            if (widget.confirmationsWidget != null)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 4),
                child: widget.confirmationsWidget!,
              ),
            for (final op in widget.operationWidgets)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 3),
                child: op,
              ),
          ],
        ],
      ),
    );
  }

  // ---------- 2. Top Finder Toolbar (52px) ----------

  Widget _buildToolbar(IcColors c, {required bool isNarrow}) {
    return Container(
      height: 52,
      padding: const EdgeInsets.fromLTRB(10, 0, 12, 0),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      child: Row(
        children: [
          _ToolbarIconBtn(
            icon: LucideIcons.chevronLeft,
            tooltip: 'Back',
            onTap: widget.onBackFolder ?? widget.onUp,
          ),
          _ToolbarIconBtn(
            icon: LucideIcons.arrowUp,
            tooltip: 'Up to parent folder',
            onTap: widget.onUp,
          ),
          const SizedBox(width: 6),
          Expanded(
            child: IcBreadcrumbBar(
              items: widget.breadcrumbs,
              neuronUri: isNarrow ? null : widget.activeNeuronUri,
              trailingActions: isNarrow
                  ? const []
                  : widget.trailingBreadcrumbWidgets,
            ),
          ),
          const SizedBox(width: 8),
          _ToolbarIconBtn(
            icon: LucideIcons.search,
            tooltip: 'Search & Filter',
            active: _showSearchDrawer,
            onTap: () => setState(() => _showSearchDrawer = !_showSearchDrawer),
          ),
          _ToolbarIconBtn(
            icon: LucideIcons.list,
            tooltip: 'View as List',
            active: !_gridView,
            onTap: () => setState(() => _gridView = false),
          ),
          _ToolbarIconBtn(
            icon: LucideIcons.layoutGrid,
            tooltip: 'View as Grid',
            active: _gridView,
            onTap: () => setState(() => _gridView = true),
          ),
          if (!isNarrow) ...[
            _ToolbarIconBtn(
              icon: LucideIcons.panelRight,
              tooltip: 'Toggle QuickView Inspector',
              active: _showInspector,
              onTap: () => setState(() => _showInspector = !_showInspector),
            ),
            Container(
              width: 1,
              height: 18,
              margin: const EdgeInsets.symmetric(horizontal: 6),
              color: c.borderHairline,
            ),
            _ToolbarIconBtn(
              icon: LucideIcons.folderPlus,
              tooltip: 'New Folder',
              active: _showAuthorDrawer && _authorMode == 'mkdir',
              onTap: widget.onNewFolder != null
                  ? () => _openAuthorDrawer('mkdir')
                  : null,
            ),
            _ToolbarIconBtn(
              icon: LucideIcons.filePlus,
              tooltip: 'New Text File',
              active: _showAuthorDrawer && _authorMode == 'write',
              onTap: widget.onWriteFile != null
                  ? () => _openAuthorDrawer('write')
                  : null,
            ),
            if (widget.onPaste != null)
              _ToolbarIconBtn(
                icon: LucideIcons.clipboardPaste,
                tooltip: 'Paste Here',
                onTap: widget.onPaste,
              ),
            if (_hasSelection && widget.onDelete != null)
              _ToolbarIconBtn(
                icon: LucideIcons.trash2,
                tooltip: 'Move to Trash',
                danger: true,
                onTap: widget.onDelete,
              ),
          ],
          const SizedBox(width: 6),
          Btn(
            label: 'Ask Ino',
            icon: LucideIcons.messagesSquare,
            small: true,
            primary: _hasSelection,
            onTap: widget.onAskIno,
          ),
        ],
      ),
    );
  }

  // ---------- 3. Search & Authoring Bars ----------

  Widget _buildSearchScopeBar(IcColors c) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      child: Wrap(
        spacing: 10,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(LucideIcons.filter, size: 13, color: c.accent),
              const SizedBox(width: 6),
              Text(
                'Filter folder:',
                style: IcType.text(
                  12,
                  weight: FontWeight.w600,
                  color: c.textMuted,
                ),
              ),
            ],
          ),
          if (widget.filterFieldWidget != null)
            SizedBox(width: 200, child: widget.filterFieldWidget!),
          Btn(label: 'Filter', small: true, onTap: widget.onApplyFilter),
          if (widget.onClearFilter != null)
            Btn(label: 'Clear', small: true, onTap: widget.onClearFilter),
          Container(width: 1, height: 18, color: c.borderHairline),
          Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(LucideIcons.globe, size: 13, color: c.accent),
              const SizedBox(width: 6),
              Text(
                'Everywhere:',
                style: IcType.text(
                  12,
                  weight: FontWeight.w600,
                  color: c.textMuted,
                ),
              ),
            ],
          ),
          if (widget.searchFieldWidget != null)
            SizedBox(width: 220, child: widget.searchFieldWidget!),
          Btn(
            label: 'Search All Storages',
            icon: LucideIcons.search,
            small: true,
            primary: true,
            onTap: widget.onSearchAll,
          ),
        ],
      ),
    );
  }

  Widget _buildAuthoringBar(IcColors c) {
    final title = switch (_authorMode) {
      'mkdir' => 'New Folder',
      'rename' => 'Rename Selected Item',
      _ => 'Create Text File',
    };
    return Container(
      padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            children: [
              Icon(
                _authorMode == 'mkdir'
                    ? LucideIcons.folderPlus
                    : (_authorMode == 'rename'
                          ? LucideIcons.pencil
                          : LucideIcons.filePlus),
                size: 14,
                color: c.accent,
              ),
              const SizedBox(width: 6),
              Text(
                title,
                style: IcType.text(
                  12.5,
                  weight: FontWeight.w700,
                  color: c.textStrong,
                ),
              ),
              const Spacer(),
              GestureDetector(
                onTap: () => setState(() => _showAuthorDrawer = false),
                child: Icon(LucideIcons.x, size: 14, color: c.textMuted),
              ),
            ],
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 10,
            runSpacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              if (widget.nameFieldWidget != null)
                SizedBox(width: 240, child: widget.nameFieldWidget!),
              if (_authorMode == 'write' && widget.contentFieldWidget != null)
                SizedBox(width: 320, child: widget.contentFieldWidget!),
              if (_authorMode == 'mkdir')
                Btn(
                  label: 'Create Folder',
                  icon: LucideIcons.folderPlus,
                  small: true,
                  primary: true,
                  onTap: widget.onNewFolder,
                ),
              if (_authorMode == 'rename')
                Btn(
                  label: 'Rename',
                  icon: LucideIcons.check,
                  small: true,
                  primary: true,
                  onTap: widget.onRename,
                ),
              if (_authorMode == 'write')
                Btn(
                  label: 'Save Text File',
                  icon: LucideIcons.save,
                  small: true,
                  primary: true,
                  onTap: widget.onWriteFile,
                ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _buildNoticeBanner(IcColors c, String message) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
      decoration: BoxDecoration(
        color: c.accentTint,
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      child: Row(
        children: [
          Icon(LucideIcons.info, size: 13, color: c.accent),
          const SizedBox(width: 8),
          Expanded(
            child: Text(message, style: IcType.text(12, color: c.textStrong)),
          ),
          if (widget.onRetry != null)
            Btn(label: 'Try Again', small: true, onTap: widget.onRetry),
        ],
      ),
    );
  }

  // ---------- 4. Main Content Area (List / Grid / Empty / Skeleton) ----------

  Widget _buildMainContent(IcColors c) {
    if (widget.isLoading) {
      return _FinderSkeletonRows(
        line: 'Reading ${_activeStorage?.label ?? 'storage'}…',
      );
    }

    if (widget.noStorageConnected) {
      return _FinderEmptyState(
        icon: LucideIcons.hardDrive,
        title: widget.statusTitle ?? 'No storage connected',
        subtitle: widget.statusBody ?? 'Connect a folder on this computer or Google Drive in Settings so Files and Ino can browse it.',
        action: widget.onOpenSettings != null
            ? Btn(
                label: 'Open Settings',
                icon: LucideIcons.settings,
                primary: true,
                onTap: widget.onOpenSettings,
              )
            : null,
      );
    }

    if (widget.statusTitle != null && widget.items.isEmpty) {
      return _FinderEmptyState(
        icon: LucideIcons.folderSearch,
        title: widget.statusTitle!,
        subtitle:
            widget.statusBody ??
            'Check the storage connection or try another search.',
        action: widget.onRetry != null
            ? Btn(
                label: 'Try Again',
                icon: LucideIcons.refreshCw,
                primary: true,
                onTap: widget.onRetry,
              )
            : null,
      );
    }

    final isAllStoragesRoot =
        widget.breadcrumbs.length <= 1 &&
        !widget.noStorageConnected &&
        widget.storages.isNotEmpty;

    if (widget.items.isEmpty && !isAllStoragesRoot) {
      return _FinderEmptyState(
        icon: LucideIcons.folderOpen,
        title: 'This folder is empty',
        subtitle: 'Create a new folder or text file, or switch to another location in the sidebar.',
        action: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (widget.onClearFilter != null) ...[
              Btn(label: 'Clear Filter', onTap: widget.onClearFilter),
              const SizedBox(width: 8),
            ],
            if (widget.onNewFolder != null)
              Btn(
                label: 'New Folder',
                icon: LucideIcons.folderPlus,
                primary: true,
                onTap: () => _openAuthorDrawer('mkdir'),
              ),
          ],
        ),
      );
    }

    final browserBody = widget.items.isEmpty
        ? Center(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Text(
                'Select a connected storage card above to browse its folders.',
                style: IcType.text(13, color: c.textMuted),
              ),
            ),
          )
        : (_gridView ? _buildGridView(c) : _buildTableListView(c));

    if (!isAllStoragesRoot) {
      return browserBody;
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        _buildAllStoragesHub(c),
        Expanded(child: browserBody),
      ],
    );
  }

  Widget _buildAllStoragesHub(IcColors c) {
    final hasGoogleDrive = widget.storages.any(
      (s) =>
          s.label.toLowerCase().contains('drive') ||
          s.icon == LucideIcons.cloud,
    );
    return Container(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            children: [
              Icon(LucideIcons.hardDrive, size: 13, color: c.accent),
              const SizedBox(width: 6),
              Text(
                'CONNECTED LOCATIONS & CLOUD DRIVES',
                style: IcType.text(
                  11,
                  weight: FontWeight.w700,
                  color: c.textMuted,
                ).copyWith(letterSpacing: 0.6),
              ),
              const Spacer(),
              if (widget.items.isNotEmpty)
                Text(
                  '${widget.items.length} contributed root ${widget.items.length == 1 ? 'folder' : 'folders'}',
                  style: IcType.text(11.5, color: c.textMuted, tnum: true),
                ),
            ],
          ),
          const SizedBox(height: 10),
          Wrap(
            spacing: 10,
            runSpacing: 10,
            children: [
              for (final s in widget.storages)
                GestureDetector(
                  onTap: s.onTap,
                  child: MouseRegion(
                    cursor: s.onTap != null
                        ? SystemMouseCursors.click
                        : SystemMouseCursors.basic,
                    child: Container(
                      width: 248,
                      padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
                      decoration: BoxDecoration(
                        color: c.surfaceCard,
                        borderRadius: BorderRadius.circular(IcRadius.card),
                        border: Border.all(
                          color: s.selected ? c.accent : c.borderHairline,
                        ),
                      ),
                      child: Row(
                        children: [
                          Container(
                            width: 34,
                            height: 34,
                            decoration: BoxDecoration(
                              color: c.accentTint,
                              borderRadius: BorderRadius.circular(IcRadius.sm),
                            ),
                            child: Icon(s.icon, size: 17, color: c.accent),
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Text(
                                  s.label,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: IcType.text(
                                    13,
                                    weight: FontWeight.w600,
                                    color: c.textStrong,
                                  ),
                                ),
                                const SizedBox(height: 2),
                                Text(
                                  s.quotaText,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: IcType.text(11.5, color: c.textMuted),
                                ),
                              ],
                            ),
                          ),
                          const SizedBox(width: 6),
                          Icon(
                            LucideIcons.chevronRight,
                            size: 14,
                            color: c.textMuted,
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
              if (!hasGoogleDrive)
                GestureDetector(
                  onTap: widget.onConnectGoogleDrive ?? widget.onOpenSettings,
                  child: MouseRegion(
                    cursor: SystemMouseCursors.click,
                    child: Container(
                      constraints: const BoxConstraints(
                        minWidth: 248,
                        maxWidth: 280,
                      ),
                      padding: const EdgeInsets.fromLTRB(10, 10, 10, 10),
                      decoration: BoxDecoration(
                        color: c.surfaceCard,
                        borderRadius: BorderRadius.circular(IcRadius.card),
                        border: Border.all(color: c.borderHairline),
                      ),
                      child: Row(
                        children: [
                          Container(
                            width: 34,
                            height: 34,
                            decoration: BoxDecoration(
                              color: c.accentTint,
                              borderRadius: BorderRadius.circular(IcRadius.sm),
                            ),
                            child: Icon(
                              LucideIcons.cloud,
                              size: 17,
                              color: c.accent,
                            ),
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Text(
                                  'Google Drive',
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: IcType.text(
                                    13,
                                    weight: FontWeight.w600,
                                    color: c.textStrong,
                                  ),
                                ),
                                const SizedBox(height: 2),
                                Text(
                                  'Connect Google to browse My Drive',
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: IcType.text(11.5, color: c.textMuted),
                                ),
                              ],
                            ),
                          ),
                          const SizedBox(width: 8),
                          Btn(
                            label: 'Connect',
                            icon: LucideIcons.plus,
                            small: true,
                            primary: true,
                            onTap:
                                widget.onConnectGoogleDrive ??
                                widget.onOpenSettings,
                          ),
                        ],
                      ),
                    ),
                  ),
                ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _buildTableListView(IcColors c) {
    Widget columnHeader(
      String label, {
      required bool active,
      VoidCallback? onTap,
      double? width,
      bool flex = false,
    }) {
      final cell = GestureDetector(
        onTap: onTap,
        child: MouseRegion(
          cursor: onTap != null
              ? SystemMouseCursors.click
              : SystemMouseCursors.basic,
          child: Row(
            children: [
              Flexible(
                child: Text(
                  label,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    12,
                    weight: FontWeight.w600,
                    color: active ? c.textStrong : c.textMuted,
                  ),
                ),
              ),
              if (active) ...[
                const SizedBox(width: 3),
                Icon(LucideIcons.chevronDown, size: 13, color: c.textStrong),
              ],
            ],
          ),
        ),
      );
      return flex ? Expanded(child: cell) : SizedBox(width: width, child: cell);
    }

    return LayoutBuilder(
      builder: (context, constraints) {
        final w = constraints.maxWidth;
        final showWhere = w >= 620;
        final showModified = w >= 460;
        final showKind = w >= 330;
        final showSize = w >= 220;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Container(
              height: 30,
              padding: const EdgeInsets.symmetric(horizontal: 16),
              decoration: BoxDecoration(
                border: Border(bottom: BorderSide(color: c.borderHairline)),
              ),
              child: Row(
                children: [
                  const SizedBox(width: 32),
                  columnHeader(
                    'Name',
                    active: widget.activeSort == 'name',
                    onTap: widget.onSortName,
                    flex: true,
                  ),
                  if (showModified)
                    columnHeader(
                      'Modified',
                      active: widget.activeSort == 'modified',
                      onTap: widget.onSortModified,
                      width: 142,
                    ),
                  if (showSize)
                    columnHeader(
                      'Size',
                      active: widget.activeSort == 'size',
                      onTap: widget.onSortSize,
                      width: 84,
                    ),
                  if (showWhere)
                    columnHeader('Where', active: false, width: 132),
                  if (showKind) columnHeader('Kind', active: false, width: 78),
                ],
              ),
            ),
            Expanded(
              child: ListView.builder(
                padding: const EdgeInsets.only(top: 4, bottom: 20),
                itemCount: widget.items.length,
                itemBuilder: (context, index) {
                  final item = widget.items[index];
                  return _FinderListRow(
                    key: ValueKey('finder-row:${item.key}'),
                    item: item,
                    odd: index.isOdd,
                    showModified: showModified,
                    showSize: showSize,
                    showWhere: showWhere,
                    showKind: showKind,
                    onContextMenu: (pos) =>
                        _showItemContextMenu(context, pos, item),
                  );
                },
              ),
            ),
          ],
        );
      },
    );
  }

  Widget _buildGridView(IcColors c) {
    return SingleChildScrollView(
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 24),
      child: Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [
          for (final item in widget.items)
            _FinderGridTile(
              key: ValueKey('finder-grid:${item.key}'),
              item: item,
              onContextMenu: (pos) => _showItemContextMenu(context, pos, item),
            ),
        ],
      ),
    );
  }

  void _showItemContextMenu(
    BuildContext context,
    Offset globalPos,
    IcFilesItemEntry item,
  ) {
    item.onSelect?.call();
    final c = IcTheme.of(context);
    final overlay =
        Overlay.of(context).context.findRenderObject() as RenderBox?;
    final position = overlay != null
        ? RelativeRect.fromRect(
            Rect.fromLTWH(globalPos.dx, globalPos.dy, 1, 1),
            Offset.zero & overlay.size,
          )
        : const RelativeRect.fromLTRB(100, 100, 100, 100);

    showMenu<String>(
      context: context,
      position: position,
      color: c.surfaceCard,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(IcRadius.button),
        side: BorderSide(color: c.borderInput),
      ),
      items: [
        PopupMenuItem<String>(
          value: 'open',
          height: 32,
          child: _MenuRow(
            icon: item.isFolder ? LucideIcons.folderOpen : LucideIcons.eye,
            label: item.isFolder ? 'Open Folder' : 'Inspect in QuickView',
          ),
        ),
        const PopupMenuItem<String>(
          value: 'ask-ino',
          height: 32,
          child: _MenuRow(
            icon: LucideIcons.messagesSquare,
            label: 'Ask Ino about this file',
          ),
        ),
        const PopupMenuDivider(height: 8),
        if (item.isFolder)
          PopupMenuItem<String>(
            value: 'fav',
            height: 32,
            child: _MenuRow(
              icon: LucideIcons.star,
              label: _favouriteFolders.contains(item.name)
                  ? 'Remove from Favourites'
                  : 'Add to Favourites',
            ),
          ),
        if (widget.activeNeuronUri != null)
          const PopupMenuItem<String>(
            value: 'copy-uri',
            height: 32,
            child: _MenuRow(
              icon: LucideIcons.cpu,
              label: 'Copy Neuron ID (neuron://)',
            ),
          ),
        if (widget.activeDisplayPath != null)
          const PopupMenuItem<String>(
            value: 'copy-path',
            height: 32,
            child: _MenuRow(icon: LucideIcons.link, label: 'Copy Display Path'),
          ),
        const PopupMenuDivider(height: 8),
        if (widget.onRename != null)
          const PopupMenuItem<String>(
            value: 'rename',
            height: 32,
            child: _MenuRow(icon: LucideIcons.pencil, label: 'Rename…'),
          ),
        if (widget.onCopy != null)
          const PopupMenuItem<String>(
            value: 'copy',
            height: 32,
            child: _MenuRow(icon: LucideIcons.copy, label: 'Copy'),
          ),
        if (widget.onMove != null)
          const PopupMenuItem<String>(
            value: 'move',
            height: 32,
            child: _MenuRow(icon: LucideIcons.folderInput, label: 'Move'),
          ),
        if (!item.isFolder && widget.onDownload != null)
          const PopupMenuItem<String>(
            value: 'download',
            height: 32,
            child: _MenuRow(icon: LucideIcons.download, label: 'Download'),
          ),
        if (widget.onDelete != null) ...[
          const PopupMenuDivider(height: 8),
          const PopupMenuItem<String>(
            value: 'delete',
            height: 32,
            child: _MenuRow(
              icon: LucideIcons.trash2,
              label: 'Move to Trash',
              danger: true,
            ),
          ),
        ],
      ],
    ).then((action) {
      if (action == null) return;
      switch (action) {
        case 'open':
          if (item.isFolder) {
            item.onOpen?.call();
          } else {
            item.onSelect?.call();
            setState(() => _showInspector = true);
          }
        case 'ask-ino':
          widget.onAskIno?.call();
        case 'fav':
          setState(() {
            if (!_favouriteFolders.remove(item.name)) {
              _favouriteFolders.add(item.name);
            }
          });
        case 'copy-uri':
          final uri = widget.selectedNeuronUri ?? widget.activeNeuronUri;
          if (uri != null) _copyToClipboard(uri, 'neuron:// URI');
        case 'copy-path':
          final path = widget.activeDisplayPath ?? item.name;
          _copyToClipboard(path, 'path');
        case 'rename':
          _openAuthorDrawer('rename');
        case 'copy':
          widget.onCopy?.call();
        case 'move':
          widget.onMove?.call();
        case 'download':
          widget.onDownload?.call();
        case 'delete':
          widget.onDelete?.call();
      }
    });
  }

  // ---------- 5. Right-Hand QuickView Inspector (300px) ----------

  Widget _buildQuickViewPane(IcColors c) {
    final selectedItem = widget.items.where((i) => i.selected).firstOrNull;
    final title = widget.selectedName ?? selectedItem?.name ?? 'QuickView';
    final uri = widget.selectedNeuronUri ?? widget.activeNeuronUri;
    final isFolder = selectedItem?.isFolder ?? false;

    return Container(
      width: 300,
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: Border(left: BorderSide(color: c.borderHairline)),
      ),
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          Row(
            children: [
              const TagPill(label: 'QuickView', tone: TagTone.neutral),
              const Spacer(),
              GestureDetector(
                onTap: () => setState(() => _showInspector = false),
                child: MouseRegion(
                  cursor: SystemMouseCursors.click,
                  child: Icon(LucideIcons.x, size: 14, color: c.textMuted),
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          Center(
            child: _FinderFileThumb(name: title, isFolder: isFolder),
          ),
          const SizedBox(height: 12),
          Text(
            title,
            textAlign: TextAlign.center,
            style: IcType.text(
              14.5,
              weight: FontWeight.w700,
              color: c.textStrong,
            ),
          ),
          if (widget.selectedMeta != null) ...[
            const SizedBox(height: 4),
            Text(
              widget.selectedMeta!,
              textAlign: TextAlign.center,
              style: IcType.text(12, color: c.textMuted, tnum: true),
            ),
          ],
          if (uri != null && uri.isNotEmpty) ...[
            const SizedBox(height: 12),
            GestureDetector(
              onTap: () => _copyToClipboard(uri, 'neuron:// URI'),
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 10,
                    vertical: 7,
                  ),
                  decoration: BoxDecoration(
                    color: c.surfaceCard,
                    borderRadius: BorderRadius.circular(IcRadius.sm),
                    border: Border.all(color: c.borderInput),
                  ),
                  child: Row(
                    children: [
                      Icon(LucideIcons.cpu, size: 13, color: c.accent),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(
                          uri,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.mono(11, color: c.accent),
                        ),
                      ),
                      const SizedBox(width: 4),
                      Icon(LucideIcons.copy, size: 12, color: c.textMuted),
                    ],
                  ),
                ),
              ),
            ),
          ],
          const SizedBox(height: 12),
          Btn(
            label: 'Ask Ino about this file',
            icon: LucideIcons.sparkles,
            small: true,
            primary: true,
            onTap: widget.onAskIno,
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              if (uri != null)
                Btn(
                  label: 'Copy URI',
                  icon: LucideIcons.copy,
                  small: true,
                  onTap: () => _copyToClipboard(uri, 'neuron:// URI'),
                ),
              if (widget.activeDisplayPath != null)
                Btn(
                  label: 'Copy Path',
                  icon: LucideIcons.link,
                  small: true,
                  onTap: () => _copyToClipboard(
                    widget.activeDisplayPath!,
                    'Display Path',
                  ),
                ),
              if (widget.onRename != null)
                Btn(
                  label: 'Rename',
                  icon: LucideIcons.pencil,
                  small: true,
                  onTap: () => _openAuthorDrawer('rename'),
                ),
              if (widget.onDelete != null)
                Btn(
                  label: 'Trash',
                  icon: LucideIcons.trash2,
                  small: true,
                  danger: true,
                  onTap: widget.onDelete,
                ),
            ],
          ),
          if (widget.inoHandoffPrompt != null &&
              widget.inoHandoffPrompt!.isNotEmpty) ...[
            const SizedBox(height: 12),
            Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: c.accentTint,
                borderRadius: BorderRadius.circular(IcRadius.sm),
                border: Border.all(color: c.accent.withValues(alpha: 0.3)),
              ),
              child: Text(
                widget.inoHandoffPrompt!,
                style: IcType.mono(11.5, color: c.textStrong),
              ),
            ),
          ],
          if (widget.previewAppCard != null) ...[
            const SizedBox(height: 12),
            widget.previewAppCard!,
          ],
          _buildQuickViewPreviewContent(c, title),
        ],
      ),
    );
  }

  // ---------- 6. Bottom Finder Status Bar (28px) ----------

  Widget _buildStatusBar(IcColors c) {
    final total = widget.items.length;
    final selectedCount = widget.items.where((i) => i.selected).length;
    final leftLabel = selectedCount > 0
        ? '$selectedCount of $total selected'
        : (total == 1 ? '1 item' : '$total items');
    final uri = widget.selectedNeuronUri ?? widget.activeNeuronUri;
    final rightQuota = _activeStorage?.quotaText ?? '';

    return Container(
      height: 28,
      padding: const EdgeInsets.symmetric(horizontal: 14),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        border: Border(top: BorderSide(color: c.borderHairline)),
      ),
      child: Row(
        children: [
          Text(
            widget.pageInfoText != null
                ? '$leftLabel (${widget.pageInfoText})'
                : leftLabel,
            style: IcType.text(12, color: c.textMuted, tnum: true),
          ),
          if (widget.onPrevPage != null ||
              widget.onNextPage != null ||
              widget.onLoadMore != null) ...[
            const SizedBox(width: 8),
            if (widget.onPrevPage != null)
              _StatusLink(label: '‹ Prev', onTap: widget.onPrevPage!),
            if (widget.onNextPage != null) ...[
              const SizedBox(width: 6),
              _StatusLink(label: 'Next ›', onTap: widget.onNextPage!),
            ],
            if (widget.onLoadMore != null) ...[
              const SizedBox(width: 6),
              _StatusLink(label: 'Load more', onTap: widget.onLoadMore!),
            ],
          ],
          if (uri != null && uri.isNotEmpty) ...[
            const SizedBox(width: 14),
            Flexible(
              child: GestureDetector(
                onTap: () => _copyToClipboard(uri, 'neuron:// URI'),
                child: MouseRegion(
                  cursor: SystemMouseCursors.click,
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(LucideIcons.cpu, size: 12, color: c.accent),
                      const SizedBox(width: 4),
                      Flexible(
                        child: Text(
                          uri,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.mono(11.5, color: c.accent),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ],
          const Spacer(),
          Text(
            rightQuota,
            style: IcType.text(12, color: c.textMuted, tnum: true),
          ),
        ],
      ),
    );
  }

  String _effectiveKind(String fileName) {
    if (widget.previewKind != null && widget.previewKind!.isNotEmpty) {
      return widget.previewKind!.toLowerCase();
    }
    final dot = fileName.lastIndexOf('.');
    final ext = dot >= 0 ? fileName.substring(dot + 1).toLowerCase() : '';
    return switch (ext) {
      'png' ||
      'jpg' ||
      'jpeg' ||
      'gif' ||
      'webp' ||
      'svg' ||
      'bmp' ||
      'ico' ||
      'avif' ||
      'tiff' ||
      'tif' => 'image',
      'mp3' || 'wav' || 'ogg' || 'flac' || 'm4a' || 'aac' || 'wma' => 'audio',
      'mp4' || 'webm' || 'mov' || 'mkv' || 'avi' || 'm4v' => 'video',
      'pdf' => 'pdf',
      'md' || 'markdown' => 'markdown',
      'cs' ||
      'dart' ||
      'ts' ||
      'js' ||
      'py' ||
      'json' ||
      'yaml' ||
      'yml' ||
      'xml' ||
      'html' ||
      'htm' ||
      'css' ||
      'scss' ||
      'sql' ||
      'sh' ||
      'ps1' ||
      'bat' ||
      'rs' ||
      'go' ||
      'java' ||
      'cpp' ||
      'c' ||
      'h' ||
      'env' ||
      'ini' ||
      'conf' => 'code',
      _ => 'text',
    };
  }

  Widget _buildQuickViewPreviewContent(IcColors c, String title) {
    final kind = _effectiveKind(title);
    final dot = title.lastIndexOf('.');
    final ext = dot >= 0 ? title.substring(dot + 1).toUpperCase() : '';

    if (kind == 'audio') {
      return Padding(
        padding: const EdgeInsets.only(top: 14),
        child: _FinderAudioPreviewCard(
          name: title,
          url: widget.previewUrl,
          colors: c,
        ),
      );
    }

    if (kind == 'video') {
      return Padding(
        padding: const EdgeInsets.only(top: 14),
        child: _FinderVideoPreviewCard(
          name: title,
          url: widget.previewUrl,
          colors: c,
        ),
      );
    }

    if (kind == 'pdf') {
      return Padding(
        padding: const EdgeInsets.only(top: 14),
        child: _FinderPdfPreviewCard(
          name: title,
          url: widget.previewUrl,
          colors: c,
        ),
      );
    }

    if (kind == 'image') {
      if (widget.previewImageWidget != null) {
        return Padding(
          padding: const EdgeInsets.only(top: 12),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(IcRadius.sm),
            child: widget.previewImageWidget!,
          ),
        );
      }
      if (widget.previewUrl != null && widget.previewUrl!.isNotEmpty) {
        return Padding(
          padding: const EdgeInsets.only(top: 12),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(IcRadius.sm),
            child: UiImage.network(url: widget.previewUrl!, caption: ''),
          ),
        );
      }
    }

    if (kind == 'markdown' &&
        widget.previewText != null &&
        widget.previewText!.isNotEmpty) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const SizedBox(height: 14),
          Text(
            'MARKDOWN PREVIEW',
            style: IcType.text(
              10.5,
              weight: FontWeight.w700,
              color: c.textMuted,
            ).copyWith(letterSpacing: 0.6),
          ),
          const SizedBox(height: 6),
          Container(
            constraints: const BoxConstraints(maxHeight: 280),
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: c.surfaceCard,
              borderRadius: BorderRadius.circular(IcRadius.sm),
              border: Border.all(color: c.borderInput),
            ),
            child: SingleChildScrollView(
              child: GptMarkdown(
                widget.previewText!,
                style: IcType.text(12, color: c.textBody, height: 1.45),
              ),
            ),
          ),
          if (widget.previewNote != null) ...[
            const SizedBox(height: 4),
            Text(
              widget.previewNote!,
              style: IcType.text(11, color: c.textMuted),
            ),
          ],
        ],
      );
    }

    if (kind == 'code' &&
        widget.previewText != null &&
        widget.previewText!.isNotEmpty) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const SizedBox(height: 14),
          Text(
            ext.isNotEmpty ? 'CODE PREVIEW ($ext)' : 'CODE PREVIEW',
            style: IcType.text(
              10.5,
              weight: FontWeight.w700,
              color: c.textMuted,
            ).copyWith(letterSpacing: 0.6),
          ),
          const SizedBox(height: 6),
          Container(
            constraints: const BoxConstraints(maxHeight: 280),
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: c.surfaceCard,
              borderRadius: BorderRadius.circular(IcRadius.sm),
              border: Border.all(color: c.borderInput),
            ),
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: SingleChildScrollView(
                child: SelectableText(
                  widget.previewText!,
                  style: IcType.mono(11.5, color: c.textBody, height: 1.45),
                ),
              ),
            ),
          ),
          if (widget.previewNote != null) ...[
            const SizedBox(height: 4),
            Text(
              widget.previewNote!,
              style: IcType.text(11, color: c.textMuted),
            ),
          ],
        ],
      );
    }

    if (widget.previewText != null && widget.previewText!.isNotEmpty) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const SizedBox(height: 14),
          Text(
            'PREVIEW',
            style: IcType.text(
              10.5,
              weight: FontWeight.w700,
              color: c.textMuted,
            ).copyWith(letterSpacing: 0.6),
          ),
          const SizedBox(height: 6),
          Container(
            constraints: const BoxConstraints(maxHeight: 280),
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              color: c.surfaceCard,
              borderRadius: BorderRadius.circular(IcRadius.sm),
              border: Border.all(color: c.borderInput),
            ),
            child: SingleChildScrollView(
              child: SelectableText(
                widget.previewText!,
                style: IcType.mono(11.5, color: c.textBody, height: 1.45),
              ),
            ),
          ),
          if (widget.previewNote != null) ...[
            const SizedBox(height: 4),
            Text(
              widget.previewNote!,
              style: IcType.text(11, color: c.textMuted),
            ),
          ],
        ],
      );
    }

    return const SizedBox.shrink();
  }
}

Uri? _resolveFinderContentUri(String? rawUrl) {
  if (rawUrl == null || rawUrl.trim().isEmpty) return null;
  final trimmed = rawUrl.trim();
  final parsed = Uri.tryParse(trimmed);
  if (parsed != null &&
      parsed.host.isNotEmpty &&
      const {'http', 'https'}.contains(parsed.scheme)) {
    return parsed;
  }
  if (trimmed.startsWith('/')) {
    try {
      final base = IntoChatOSHostEnv.requireUiBaseUri();
      return base.resolve(trimmed);
    } catch (_) {
      return Uri.tryParse('http://127.0.0.1:5000$trimmed');
    }
  }
  return null;
}

class _FinderAudioPreviewCard extends StatefulWidget {
  const _FinderAudioPreviewCard({
    required this.name,
    required this.url,
    required this.colors,
  });

  final String name;
  final String? url;
  final IcColors colors;

  @override
  State<_FinderAudioPreviewCard> createState() =>
      _FinderAudioPreviewCardState();
}

class _FinderAudioPreviewCardState extends State<_FinderAudioPreviewCard> {
  bool _isPlaying = false;

  @override
  Widget build(BuildContext context) {
    final c = widget.colors;
    final dot = widget.name.lastIndexOf('.');
    final ext = dot >= 0
        ? widget.name.substring(dot + 1).toUpperCase()
        : 'AUDIO';

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(IcRadius.sm),
        border: Border.all(color: c.borderInput),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Container(
                width: 36,
                height: 36,
                decoration: BoxDecoration(
                  color: c.accentTint,
                  borderRadius: BorderRadius.circular(IcRadius.xs),
                ),
                child: Icon(LucideIcons.fileAudio, size: 20, color: c.accent),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      widget.name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        12.5,
                        weight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      '$ext Audio Track',
                      style: IcType.text(11, color: c.textMuted),
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              GestureDetector(
                onTap: () {
                  setState(() => _isPlaying = !_isPlaying);
                  if (widget.url != null) {
                    final uri = _resolveFinderContentUri(widget.url);
                    if (uri != null) {
                      launchUrl(uri, mode: LaunchMode.externalApplication);
                    }
                  }
                },
                child: Container(
                  width: 32,
                  height: 32,
                  decoration: BoxDecoration(
                    color: c.accent,
                    shape: BoxShape.circle,
                  ),
                  child: Icon(
                    _isPlaying ? LucideIcons.pause : LucideIcons.play,
                    size: 16,
                    color: c.onAccent,
                  ),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: ClipRRect(
                  borderRadius: BorderRadius.circular(2),
                  child: LinearProgressIndicator(
                    value: _isPlaying ? 0.35 : 0.0,
                    backgroundColor: c.borderHairline,
                    valueColor: AlwaysStoppedAnimation(c.accent),
                    minHeight: 4,
                  ),
                ),
              ),
              const SizedBox(width: 8),
              Text(
                _isPlaying ? 'Playing' : 'Ready',
                style: IcType.mono(10.5, color: c.textMuted),
              ),
            ],
          ),
          if (widget.url != null) ...[
            const SizedBox(height: 10),
            Btn(
              label: 'Open in Media Player',
              icon: LucideIcons.externalLink,
              small: true,
              onTap: () {
                final uri = _resolveFinderContentUri(widget.url);
                if (uri != null) {
                  launchUrl(uri, mode: LaunchMode.externalApplication);
                }
              },
            ),
          ],
        ],
      ),
    );
  }
}

class _FinderVideoPreviewCard extends StatelessWidget {
  const _FinderVideoPreviewCard({
    required this.name,
    required this.url,
    required this.colors,
  });

  final String name;
  final String? url;
  final IcColors colors;

  @override
  Widget build(BuildContext context) {
    final c = colors;
    final dot = name.lastIndexOf('.');
    final ext = dot >= 0 ? name.substring(dot + 1).toUpperCase() : 'VIDEO';

    return Container(
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(IcRadius.sm),
        border: Border.all(color: c.borderInput),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          AspectRatio(
            aspectRatio: 16 / 9,
            child: Container(
              decoration: BoxDecoration(
                color: Colors.black.withValues(alpha: 0.85),
                borderRadius: const BorderRadius.vertical(
                  top: Radius.circular(IcRadius.sm),
                ),
              ),
              child: Center(
                child: GestureDetector(
                  onTap: () {
                    final uri = _resolveFinderContentUri(url);
                    if (uri != null) {
                      launchUrl(uri, mode: LaunchMode.externalApplication);
                    }
                  },
                  child: Container(
                    width: 44,
                    height: 44,
                    decoration: BoxDecoration(
                      color: c.accent.withValues(alpha: 0.9),
                      shape: BoxShape.circle,
                    ),
                    child: Icon(LucideIcons.play, size: 22, color: c.onAccent),
                  ),
                ),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.all(10),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    12.5,
                    weight: FontWeight.w600,
                    color: c.textStrong,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  '$ext Video Stream',
                  style: IcType.text(11, color: c.textMuted),
                ),
                if (url != null) ...[
                  const SizedBox(height: 8),
                  Btn(
                    label: 'Play Video',
                    icon: LucideIcons.externalLink,
                    small: true,
                    primary: true,
                    onTap: () {
                      final uri = _resolveFinderContentUri(url);
                      if (uri != null) {
                        launchUrl(uri, mode: LaunchMode.externalApplication);
                      }
                    },
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _FinderPdfPreviewCard extends StatelessWidget {
  const _FinderPdfPreviewCard({
    required this.name,
    required this.url,
    required this.colors,
  });

  final String name;
  final String? url;
  final IcColors colors;

  @override
  Widget build(BuildContext context) {
    final c = colors;
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(IcRadius.sm),
        border: Border.all(color: c.borderInput),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Container(
                width: 36,
                height: 36,
                decoration: BoxDecoration(
                  color: Colors.red.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(IcRadius.xs),
                ),
                child: const Icon(
                  LucideIcons.fileSpreadsheet,
                  size: 20,
                  color: Colors.red,
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        12.5,
                        weight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      'PDF Document',
                      style: IcType.text(11, color: c.textMuted),
                    ),
                  ],
                ),
              ),
            ],
          ),
          if (url != null) ...[
            const SizedBox(height: 10),
            Btn(
              label: 'Open Document',
              icon: LucideIcons.fileSpreadsheet,
              small: true,
              primary: true,
              onTap: () {
                final uri = _resolveFinderContentUri(url);
                if (uri != null) {
                  launchUrl(uri, mode: LaunchMode.externalApplication);
                }
              },
            ),
          ],
        ],
      ),
    );
  }
}

// ============================================================================
// Finder Subcomponents (matching E:\intochat\design\apps\os\lib\apps\files\)
// ============================================================================

IconData _fileKindIcon(String name, {required bool isFolder}) {
  if (isFolder) return LucideIcons.folder;
  final dot = name.lastIndexOf('.');
  final ext = dot >= 0 ? name.substring(dot + 1).toLowerCase() : '';
  return switch (ext) {
    'md' || 'txt' || 'rtf' || 'doc' || 'docx' => LucideIcons.fileText,
    'pdf' => LucideIcons.fileSpreadsheet,
    'png' ||
    'jpg' ||
    'jpeg' ||
    'gif' ||
    'webp' ||
    'svg' => LucideIcons.fileImage,
    'cs' ||
    'dart' ||
    'ts' ||
    'js' ||
    'py' ||
    'json' ||
    'yaml' ||
    'yml' ||
    'xml' ||
    'html' ||
    'css' => LucideIcons.fileCode,
    'zip' || 'tar' || 'gz' || '7z' || 'nupkg' => LucideIcons.fileArchive,
    'mp3' || 'wav' || 'ogg' => LucideIcons.fileAudio,
    'mp4' || 'mov' || 'webm' => LucideIcons.fileVideo,
    _ => LucideIcons.file,
  };
}

class _FinderSideRow extends StatefulWidget {
  const _FinderSideRow({
    required this.icon,
    required this.label,
    required this.selected,
    this.itemKey,
    this.detail,
    this.indent = 0,
    this.dim = false,
    this.onTap,
  });

  final Key? itemKey;
  final IconData icon;
  final String label;
  final String? detail;
  final double indent;
  final bool selected;
  final bool dim;
  final VoidCallback? onTap;

  @override
  State<_FinderSideRow> createState() => _FinderSideRowState();
}

class _FinderSideRowState extends State<_FinderSideRow> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final sel = widget.selected;
    final fg = sel
        ? c.onAccent
        : widget.dim
        ? c.textSubtle
        : c.textStrong;
    final sub = sel ? c.onAccent.withValues(alpha: 0.82) : c.textMuted;

    return MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      cursor: widget.onTap != null
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      child: GestureDetector(
        key: widget.itemKey,
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: IcMotion.fast,
          constraints: const BoxConstraints(minHeight: 30),
          margin: const EdgeInsets.symmetric(vertical: 1),
          padding: EdgeInsets.fromLTRB(8 + widget.indent, 5, 8, 5),
          decoration: BoxDecoration(
            color: sel
                ? c.accent
                : (_hover && widget.onTap != null
                      ? c.surfaceCard
                      : Colors.transparent),
            borderRadius: BorderRadius.circular(IcRadius.sm),
          ),
          child: Row(
            children: [
              Icon(
                widget.icon,
                size: 15,
                color: sel
                    ? c.onAccent
                    : (widget.dim ? c.textSubtle : c.accent),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      widget.label,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: IcType.text(
                        13,
                        weight: sel ? FontWeight.w600 : FontWeight.w500,
                        color: fg,
                      ),
                    ),
                    if (widget.detail != null && widget.detail!.isNotEmpty)
                      Text(
                        widget.detail!,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: IcType.text(11, color: sub, tnum: true),
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

class _FinderListRow extends StatefulWidget {
  const _FinderListRow({
    super.key,
    required this.item,
    required this.odd,
    required this.showModified,
    required this.showSize,
    required this.showWhere,
    required this.showKind,
    required this.onContextMenu,
  });

  final IcFilesItemEntry item;
  final bool odd;
  final bool showModified;
  final bool showSize;
  final bool showWhere;
  final bool showKind;
  final ValueChanged<Offset> onContextMenu;

  @override
  State<_FinderListRow> createState() => _FinderListRowState();
}

class _FinderListRowState extends State<_FinderListRow> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final item = widget.item;
    final sel = item.selected;
    final strong = sel ? c.onAccent : c.textStrong;
    final muted = sel ? c.onAccent.withValues(alpha: 0.88) : c.textMuted;
    final bg = sel
        ? c.accent
        : (_hover
              ? c.surfaceCard
              : (widget.odd
                    ? c.surfaceSunk.withValues(alpha: 0.45)
                    : Colors.transparent));

    return MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: item.isFolder ? item.onOpen : (item.onSelect ?? item.onOpen),
        onSecondaryTapUp: (d) => widget.onContextMenu(d.globalPosition),
        child: AnimatedContainer(
          duration: IcMotion.fast,
          height: 32,
          margin: const EdgeInsets.symmetric(horizontal: 6),
          padding: const EdgeInsets.symmetric(horizontal: 10),
          decoration: BoxDecoration(
            color: bg,
            borderRadius: BorderRadius.circular(IcRadius.xs),
          ),
          child: Row(
            children: [
              SizedBox(
                width: 30,
                child: Align(
                  alignment: Alignment.centerLeft,
                  child: Icon(
                    _fileKindIcon(item.name, isFolder: item.isFolder),
                    size: 17,
                    color: sel
                        ? c.onAccent
                        : (item.isFolder ? c.accent : c.textBody),
                  ),
                ),
              ),
              Expanded(
                child: Text(
                  item.name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    13.5,
                    weight: FontWeight.w500,
                    color: strong,
                  ),
                ),
              ),
              if (widget.showModified)
                SizedBox(
                  width: 142,
                  child: Text(
                    item.modifiedLabel,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: IcType.text(12.5, color: muted, tnum: true),
                  ),
                ),
              if (widget.showSize)
                SizedBox(
                  width: 84,
                  child: Text(
                    item.sizeLabel,
                    maxLines: 1,
                    style: IcType.text(12.5, color: muted, tnum: true),
                  ),
                ),
              if (widget.showWhere)
                SizedBox(
                  width: 132,
                  child: Text(
                    item.whereLabel,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: IcType.text(12.5, color: muted),
                  ),
                ),
              if (widget.showKind)
                SizedBox(
                  width: 78,
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.spaceBetween,
                    children: [
                      Flexible(
                        child: Text(
                          item.kindLabel,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.text(12, color: muted),
                        ),
                      ),
                      if (item.isFolder && item.onSelect != null)
                        GestureDetector(
                          onTap: item.onSelect,
                          child: Tooltip(
                            message: 'Inspect folder',
                            child: Icon(
                              LucideIcons.info,
                              size: 13,
                              color: muted,
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
}

class _FinderGridTile extends StatefulWidget {
  const _FinderGridTile({
    super.key,
    required this.item,
    required this.onContextMenu,
  });

  final IcFilesItemEntry item;
  final ValueChanged<Offset> onContextMenu;

  @override
  State<_FinderGridTile> createState() => _FinderGridTileState();
}

class _FinderGridTileState extends State<_FinderGridTile> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final item = widget.item;
    final lit = item.selected;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: item.isFolder ? item.onOpen : (item.onSelect ?? item.onOpen),
        onSecondaryTapUp: (d) => widget.onContextMenu(d.globalPosition),
        child: AnimatedContainer(
          duration: IcMotion.fast,
          width: 112,
          height: 132,
          padding: const EdgeInsets.fromLTRB(8, 12, 8, 8),
          decoration: BoxDecoration(
            color: lit
                ? c.accentTint
                : (_hover ? c.surfaceCard : Colors.transparent),
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(
              color: lit ? c.accent.withValues(alpha: 0.6) : Colors.transparent,
              width: 1.5,
            ),
          ),
          child: Column(
            children: [
              SizedBox(
                height: 64,
                child: Center(
                  child: _FinderFileThumb(
                    name: item.name,
                    isFolder: item.isFolder,
                  ),
                ),
              ),
              const SizedBox(height: 8),
              Text(
                item.name,
                maxLines: 2,
                textAlign: TextAlign.center,
                overflow: TextOverflow.ellipsis,
                style: IcType.text(
                  12.5,
                  weight: FontWeight.w600,
                  color: c.textStrong,
                  height: 1.25,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _FinderFileThumb extends StatelessWidget {
  const _FinderFileThumb({required this.name, required this.isFolder});

  final String name;
  final bool isFolder;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    if (isFolder) {
      return Icon(LucideIcons.folder, size: 48, color: c.accent);
    }
    final dot = name.lastIndexOf('.');
    final ext = dot >= 0 ? name.substring(dot + 1).toLowerCase() : '';
    if (const {'png', 'jpg', 'jpeg', 'gif', 'webp', 'svg'}.contains(ext)) {
      return Container(
        width: 64,
        height: 48,
        clipBehavior: Clip.antiAlias,
        decoration: BoxDecoration(
          color: c.accentTint,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(color: c.borderInput),
        ),
        child: Stack(
          children: [
            Positioned(
              left: 8,
              top: 7,
              child: Container(
                width: 10,
                height: 10,
                decoration: BoxDecoration(
                  color: c.working,
                  shape: BoxShape.circle,
                ),
              ),
            ),
            Positioned(
              left: -6,
              right: 18,
              bottom: -10,
              child: Container(
                height: 28,
                decoration: BoxDecoration(
                  color: c.accent,
                  borderRadius: BorderRadius.circular(14),
                ),
              ),
            ),
            Positioned(
              left: 22,
              right: -10,
              bottom: -14,
              child: Container(
                height: 30,
                decoration: BoxDecoration(
                  color: c.accentHover,
                  borderRadius: BorderRadius.circular(15),
                ),
              ),
            ),
          ],
        ),
      );
    }
    if (const {'pdf', 'md', 'txt', 'doc', 'docx'}.contains(ext)) {
      return Container(
        width: 46,
        height: 58,
        padding: const EdgeInsets.fromLTRB(7, 9, 7, 7),
        decoration: BoxDecoration(
          color: c.surfaceCard,
          borderRadius: BorderRadius.circular(3),
          border: Border.all(color: c.borderInput),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(height: 4, width: 22, color: c.textStrong),
            const SizedBox(height: 5),
            for (final w in [30.0, 28.0, 31.0, 24.0, 18.0])
              Padding(
                padding: const EdgeInsets.only(bottom: 3),
                child: Container(height: 2, width: w, color: c.borderInput),
              ),
          ],
        ),
      );
    }
    return Icon(
      _fileKindIcon(name, isFolder: false),
      size: 40,
      color: c.textBody,
    );
  }
}

class _FinderEmptyState extends StatelessWidget {
  const _FinderEmptyState({
    required this.icon,
    required this.title,
    required this.subtitle,
    this.action,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 56,
              height: 56,
              alignment: Alignment.center,
              decoration: BoxDecoration(
                color: c.surfaceSunk,
                borderRadius: BorderRadius.circular(IcRadius.card),
                border: Border.all(color: c.borderHairline),
              ),
              child: Icon(icon, size: 24, color: c.textBody),
            ),
            const SizedBox(height: 14),
            Text(
              title,
              textAlign: TextAlign.center,
              style: IcType.text(
                16,
                weight: FontWeight.w600,
                color: c.textStrong,
              ),
            ),
            const SizedBox(height: 4),
            ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 380),
              child: Text(
                subtitle,
                textAlign: TextAlign.center,
                style: IcType.text(14, color: c.textMuted, height: 1.4),
              ),
            ),
            if (action != null) ...[const SizedBox(height: 16), action!],
          ],
        ),
      ),
    );
  }
}

class _FinderSkeletonRows extends StatelessWidget {
  const _FinderSkeletonRows({required this.line});

  final String line;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 0),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              SizedBox(
                width: 14,
                height: 14,
                child: CircularProgressIndicator(
                  strokeWidth: 2,
                  color: c.accent,
                ),
              ),
              const SizedBox(width: 8),
              Text(
                line,
                style: IcType.text(
                  13,
                  weight: FontWeight.w500,
                  color: c.textBody,
                  tnum: true,
                ),
              ),
            ],
          ),
          const SizedBox(height: 14),
          for (final w in [0.62, 0.48, 0.71, 0.4, 0.55, 0.66])
            Container(
              height: 30,
              decoration: BoxDecoration(
                border: Border(bottom: BorderSide(color: c.borderHairline)),
              ),
              child: Row(
                children: [
                  Container(
                    width: 16,
                    height: 16,
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(4),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: FractionallySizedBox(
                      alignment: Alignment.centerLeft,
                      widthFactor: w,
                      child: Container(
                        height: 10,
                        decoration: BoxDecoration(
                          color: c.surfaceSunk,
                          borderRadius: BorderRadius.circular(4),
                        ),
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

class _ToolbarIconBtn extends StatefulWidget {
  const _ToolbarIconBtn({
    required this.icon,
    required this.tooltip,
    this.active = false,
    this.danger = false,
    this.onTap,
  });

  final IconData icon;
  final String tooltip;
  final bool active;
  final bool danger;
  final VoidCallback? onTap;

  @override
  State<_ToolbarIconBtn> createState() => _ToolbarIconBtnState();
}

class _ToolbarIconBtnState extends State<_ToolbarIconBtn> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final enabled = widget.onTap != null;
    final fg = !enabled
        ? c.textSubtle
        : widget.danger
        ? c.error
        : widget.active
        ? c.accent
        : c.textBody;

    return Tooltip(
      message: widget.tooltip,
      child: MouseRegion(
        onEnter: (_) => setState(() => _hover = true),
        onExit: (_) => setState(() => _hover = false),
        cursor: enabled ? SystemMouseCursors.click : SystemMouseCursors.basic,
        child: GestureDetector(
          onTap: widget.onTap,
          child: AnimatedContainer(
            duration: IcMotion.fast,
            width: 30,
            height: 30,
            margin: const EdgeInsets.symmetric(horizontal: 1),
            alignment: Alignment.center,
            decoration: BoxDecoration(
              color: widget.active
                  ? c.accentTint
                  : (_hover && enabled ? c.surfaceSunk : Colors.transparent),
              borderRadius: BorderRadius.circular(IcRadius.xs),
            ),
            child: Icon(widget.icon, size: 15, color: fg),
          ),
        ),
      ),
    );
  }
}

class _MenuRow extends StatelessWidget {
  const _MenuRow({
    required this.icon,
    required this.label,
    this.danger = false,
  });

  final IconData icon;
  final String label;
  final bool danger;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final color = danger ? c.error : c.textStrong;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 14, color: danger ? c.error : c.textMuted),
        const SizedBox(width: 8),
        Text(
          label,
          style: IcType.text(12.5, weight: FontWeight.w500, color: color),
        ),
      ],
    );
  }
}

class _StatusLink extends StatelessWidget {
  const _StatusLink({required this.label, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return GestureDetector(
      onTap: onTap,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: Text(
          label,
          style: IcType.text(12, weight: FontWeight.w600, color: c.accent),
        ),
      ),
    );
  }
}
