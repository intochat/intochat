part of 'ic_files_finder_view.dart';

extension _IcFilesFinderQuickView on _IcFilesFinderViewState {
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
                onTap: () => _setShowInspector(false),
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
