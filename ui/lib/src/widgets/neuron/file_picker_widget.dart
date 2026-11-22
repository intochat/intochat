import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../design/tokens.dart';
import '../../primitives/primitives.dart';
import 'neuron_widget.dart';

/// Reactive WinUI 3 / Files VFS Picker presenter for `IntoChat.OS.Modules.UI.FilePicker.IFilePicker`.
class FilePickerNeuronWidget extends StatefulWidget {
  const FilePickerNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<FilePickerNeuronWidget> createState() => _FilePickerNeuronWidgetState();
}

class _FilePickerNeuronWidgetState extends State<FilePickerNeuronWidget> {
  final TextEditingController _filterController = TextEditingController();

  @override
  void dispose() {
    _filterController.dispose();
    super.dispose();
  }

  static bool _matchesFilter(Map<String, dynamic> entry, String filter) {
    final trimmed = filter.trim();
    if (trimmed.isEmpty || trimmed == '*' || trimmed == '*.*') return true;
    if (trimmed.startsWith('*.') || trimmed.startsWith('.')) {
      if (entry['isDirectory'] == true) return true;
      final ext = trimmed.startsWith('*.') ? trimmed.substring(1) : trimmed;
      final name = '${entry['name'] ?? ''}'.toLowerCase();
      return name.endsWith(ext.toLowerCase());
    }
    final q = trimmed.toLowerCase();
    final name = '${entry['name'] ?? ''}'.toLowerCase();
    final path = '${entry['path'] ?? ''}'.toLowerCase();
    return name.contains(q) || path.contains(q);
  }

  static String _formatSize(int bytes) {
    if (bytes <= 0) return '';
    if (bytes < 1024) return '$bytes B';
    if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
    return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.file-picker',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final title = (state['title'] as String?) ?? 'Select file';
        final mode = (state['mode'] as String?) ?? 'open';
        final currentPath = (state['currentPath'] as String?) ?? '/';
        final activeFilter = (state['activeFilter'] as String?) ?? '*';
        final confirmedPath = state['confirmedPath'] as String?;
        final filters = state['filters'] is List
            ? [for (final f in state['filters'] as List) '$f']
            : const <String>['*'];
        final selected = state['selectedPaths'] is List
            ? {for (final p in state['selectedPaths'] as List) '$p'}
            : const <String>{};
        final rawEntries = (state['entries'] as List?) ?? const [];
        final entries = <Map<String, dynamic>>[
          for (final item in rawEntries)
            if (item is Map &&
                _matchesFilter(Map<String, dynamic>.from(item), activeFilter))
              Map<String, dynamic>.from(item),
        ];

        final displayFilter = activeFilter == '*' ? '' : activeFilter;
        if (_filterController.text != displayFilter &&
            !FocusScope.of(context).hasFocus) {
          _filterController.text = displayFilter;
        }

        final canConfirm = selected.isNotEmpty || mode == 'folder';

        return Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: [
              Row(
                children: [
                  Icon(LucideIcons.folderOpen, size: 15, color: c.accent),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      title,
                      style: TextStyle(
                        fontSize: 12.5,
                        fontWeight: FontWeight.w600,
                        color: c.textStrong,
                      ),
                    ),
                  ),
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 2,
                    ),
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Text(
                      currentPath,
                      style: TextStyle(
                        fontFamily: 'JetBrains Mono',
                        fontSize: 10.5,
                        color: c.textMuted,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              SizedBox(
                height: 32,
                child: TextField(
                  controller: _filterController,
                  style: TextStyle(fontSize: 12, color: c.textStrong),
                  decoration: InputDecoration(
                    isDense: true,
                    hintText: 'Filter files or extensionâ€¦',
                    hintStyle: TextStyle(fontSize: 12, color: c.textMuted),
                    prefixIcon: Icon(
                      LucideIcons.search,
                      size: 14,
                      color: c.textMuted,
                    ),
                    filled: true,
                    fillColor: c.surfaceSunk,
                    contentPadding: const EdgeInsets.symmetric(
                      horizontal: 8,
                      vertical: 6,
                    ),
                    border: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(IcRadius.xs),
                      borderSide: BorderSide(color: c.borderHairline),
                    ),
                    enabledBorder: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(IcRadius.xs),
                      borderSide: BorderSide(color: c.borderHairline),
                    ),
                  ),
                  onChanged: (q) => unawaited(
                    invoke('SetFilter', [q.trim().isEmpty ? '*' : q]),
                  ),
                  onSubmitted: (q) => unawaited(
                    invoke('SetFilter', [q.trim().isEmpty ? '*' : q]),
                  ),
                ),
              ),
              if (filters.length > 1) ...[
                const SizedBox(height: 6),
                Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final f in filters)
                      UiMixChip(
                        label: f,
                        selected: f == activeFilter,
                        onSelected: (_) => unawaited(invoke('SetFilter', [f])),
                      ),
                  ],
                ),
              ],
              const SizedBox(height: 8),
              Container(
                constraints: const BoxConstraints(maxHeight: 200),
                decoration: BoxDecoration(
                  color: c.surfaceSunk,
                  borderRadius: BorderRadius.circular(IcRadius.sm),
                  border: Border.all(color: c.borderHairline),
                ),
                child: entries.isEmpty
                    ? Padding(
                        padding: const EdgeInsets.all(16),
                        child: Text(
                          'No matching entries in $currentPath',
                          textAlign: TextAlign.center,
                          style: TextStyle(fontSize: 12, color: c.textMuted),
                        ),
                      )
                    : SingleChildScrollView(
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            for (final entry in entries)
                              Builder(
                                builder: (context) {
                                  final path = '${entry['path'] ?? ''}';
                                  final name = '${entry['name'] ?? path}';
                                  final isDir = entry['isDirectory'] == true;
                                  final size =
                                      (entry['sizeBytes'] as num?)?.toInt() ??
                                      0;
                                  final isSel = selected.contains(path);

                                  return Material(
                                    color: isSel
                                        ? c.accentTint
                                        : Colors.transparent,
                                    child: InkWell(
                                      onTap: () =>
                                          unawaited(invoke('Select', [path])),
                                      child: Padding(
                                        padding: const EdgeInsets.symmetric(
                                          horizontal: 10,
                                          vertical: 7,
                                        ),
                                        child: Row(
                                          children: [
                                            Icon(
                                              isDir
                                                  ? LucideIcons.folder
                                                  : LucideIcons.fileText,
                                              size: 14,
                                              color: isSel
                                                  ? c.accent
                                                  : c.textMuted,
                                            ),
                                            const SizedBox(width: 8),
                                            Expanded(
                                              child: Text(
                                                name,
                                                style: TextStyle(
                                                  fontSize: 12.5,
                                                  fontWeight: isSel
                                                      ? FontWeight.w600
                                                      : FontWeight.w400,
                                                  color: isSel
                                                      ? c.accent
                                                      : c.textStrong,
                                                ),
                                              ),
                                            ),
                                            if (!isDir && size > 0)
                                              Text(
                                                _formatSize(size),
                                                style: TextStyle(
                                                  fontSize: 11,
                                                  color: c.textMuted,
                                                ),
                                              ),
                                          ],
                                        ),
                                      ),
                                    ),
                                  );
                                },
                              ),
                          ],
                        ),
                      ),
              ),
              const SizedBox(height: 8),
              Row(
                children: [
                  Expanded(
                    child: Text(
                      confirmedPath != null
                          ? 'Selected: $confirmedPath'
                          : selected.isEmpty
                          ? 'Choose an entry'
                          : '${selected.length} selected',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(fontSize: 11.5, color: c.textMuted),
                    ),
                  ),
                  FilledButton(
                    onPressed: canConfirm
                        ? () => unawaited(invoke('Confirm', const []))
                        : null,
                    child: Text(mode == 'save' ? 'Save' : 'Open'),
                  ),
                ],
              ),
            ],
          ),
        );
      },
    );
  }
}
