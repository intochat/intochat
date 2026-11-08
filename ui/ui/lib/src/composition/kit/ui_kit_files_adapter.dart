part of '../neuron_view.dart';

extension _UiKitFilesAdapter on ImmutableView {
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
            final tk = tkKey(tb);
            final isBtnEnabled = tb['enabled'] != false;
            switch (tk) {
              case 'upload':
                if (isBtnEnabled) onUpload = _dispatchButton(context, tb);
              case 'mkdir':
                if (isBtnEnabled) onNewFolder = _dispatchButton(context, tb);
              case 'rename':
                if (isBtnEnabled) onRename = _dispatchButton(context, tb);
              case 'move':
                if (isBtnEnabled) onMove = _dispatchButton(context, tb);
              case 'copy':
                if (isBtnEnabled) onCopy = _dispatchButton(context, tb);
              case 'ask-ino-toolbar':
                if (isBtnEnabled) onAskIno ??= _dispatchButton(context, tb);
              case 'download':
                if (isBtnEnabled) onDownload = _dispatchButton(context, tb);
              case 'delete':
                if (isBtnEnabled) onDelete = _dispatchButton(context, tb);
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

  static String tkKey(Map tb) => tb['key'] as String? ?? '';
}
