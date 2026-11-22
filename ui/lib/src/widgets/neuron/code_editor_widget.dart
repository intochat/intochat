import 'dart:async';

import 'package:flutter/material.dart';
import 'package:re_editor/re_editor.dart';
import 'package:re_highlight/languages/csharp.dart';
import 'package:re_highlight/languages/dart.dart';
import 'package:re_highlight/languages/json.dart';
import 'package:re_highlight/languages/markdown.dart';
import 'package:re_highlight/languages/sql.dart';
import 'package:re_highlight/languages/typescript.dart';
import 'package:re_highlight/languages/yaml.dart';
import 'package:re_highlight/re_highlight.dart';

import '../../design/tokens.dart';
import 'neuron_widget.dart';

/// Canonical language modes supported by [CodeEditorNeuronWidget] (`re_highlight`).
final Map<String, Mode> supportedCodeHighlightModes = <String, Mode>{
  'csharp': langCsharp,
  'dart': langDart,
  'json': langJson,
  'sql': langSql,
  'typescript': langTypescript,
  'yaml': langYaml,
  'markdown': langMarkdown,
};

/// Resolves raw language names or file extensions to one of the 7 supported
/// `re_highlight` grammar keys (`csharp`, `dart`, `json`, `sql`, `typescript`,
/// `yaml`, `markdown`), `'auto'`, or `null` for plain text.
String? resolveCodeHighlightLanguage(String rawLanguage, {String title = ''}) {
  final norm = rawLanguage.trim().toLowerCase();
  final direct = switch (norm) {
    'csharp' || 'cs' || 'c#' || 'dotnet' => 'csharp',
    'dart' || 'flutter' => 'dart',
    'json' || 'jsonc' || 'jsonl' => 'json',
    'sql' || 'postgres' || 'postgresql' || 'sqlite' || 'tsql' => 'sql',
    'typescript' ||
    'ts' ||
    'tsx' ||
    'javascript' ||
    'js' ||
    'jsx' => 'typescript',
    'yaml' || 'yml' => 'yaml',
    'markdown' || 'md' || 'mdx' => 'markdown',
    'auto' => 'auto',
    'text' || 'plaintext' || 'txt' => null,
    _ => '',
  };
  if (direct != '') return direct;

  final lowerTitle = title.trim().toLowerCase();
  if (lowerTitle.endsWith('.cs') || lowerTitle.endsWith('.csx')) {
    return 'csharp';
  }
  if (lowerTitle.endsWith('.dart')) return 'dart';
  if (lowerTitle.endsWith('.json') || lowerTitle.endsWith('.jsonl')) {
    return 'json';
  }
  if (lowerTitle.endsWith('.sql')) return 'sql';
  if (lowerTitle.endsWith('.ts') ||
      lowerTitle.endsWith('.tsx') ||
      lowerTitle.endsWith('.js') ||
      lowerTitle.endsWith('.jsx')) {
    return 'typescript';
  }
  if (lowerTitle.endsWith('.yaml') || lowerTitle.endsWith('.yml')) {
    return 'yaml';
  }
  if (lowerTitle.endsWith('.md') || lowerTitle.endsWith('.markdown')) {
    return 'markdown';
  }
  return null;
}

/// Builds an adaptive light/dark [CodeHighlightTheme] harmonized with [IcColors].
CodeHighlightTheme? buildIcCodeHighlightTheme(
  IcColors c,
  String rawLanguage, {
  String title = '',
}) {
  final resolved = resolveCodeHighlightLanguage(rawLanguage, title: title);
  if (resolved == null) return null;

  final Map<String, CodeHighlightThemeMode> languages;
  if (resolved == 'auto') {
    languages = {
      for (final entry in supportedCodeHighlightModes.entries)
        entry.key: CodeHighlightThemeMode(mode: entry.value),
    };
  } else {
    final mode = supportedCodeHighlightModes[resolved];
    if (mode == null) return null;
    languages = {resolved: CodeHighlightThemeMode(mode: mode)};
  }

  final keywordColor = c.isDark
      ? const Color(0xFFC792EA)
      : const Color(0xFF7C3AED);
  final typeColor = c.isDark
      ? const Color(0xFF4FD1C5)
      : const Color(0xFF0D9488);
  final attrColor = c.isDark
      ? const Color(0xFF9CDCFE)
      : const Color(0xFF1E40AF);

  final styles = <String, TextStyle>{
    'root': TextStyle(color: c.textStrong, backgroundColor: c.surfaceSunk),
    'keyword': TextStyle(color: keywordColor, fontWeight: FontWeight.w600),
    'operator': TextStyle(color: keywordColor),
    'pattern-match': TextStyle(
      color: keywordColor,
      fontWeight: FontWeight.w600,
    ),
    'selector-tag': TextStyle(color: keywordColor, fontWeight: FontWeight.w600),
    'deletion': TextStyle(color: c.error),
    'string': TextStyle(color: c.done),
    'regexp': TextStyle(color: c.done),
    'addition': TextStyle(color: c.done),
    'attribute': TextStyle(color: c.done),
    'meta-string': TextStyle(color: c.done),
    'number': TextStyle(color: c.working),
    'literal': TextStyle(color: c.working, fontWeight: FontWeight.w600),
    'type': TextStyle(color: typeColor),
    'built_in': TextStyle(color: typeColor),
    'class-title': TextStyle(color: typeColor, fontWeight: FontWeight.w600),
    'title.class_': TextStyle(color: typeColor, fontWeight: FontWeight.w600),
    'function': TextStyle(color: c.accent),
    'title': TextStyle(color: c.accent, fontWeight: FontWeight.w600),
    'title.function_': TextStyle(color: c.accent),
    'section': TextStyle(color: c.accent, fontWeight: FontWeight.w700),
    'symbol': TextStyle(color: c.accent),
    'bullet': TextStyle(color: c.accent, fontWeight: FontWeight.w700),
    'link': TextStyle(color: c.accent, decoration: TextDecoration.underline),
    'attr': TextStyle(color: attrColor),
    'name': TextStyle(color: attrColor),
    'variable': TextStyle(color: attrColor),
    'template-variable': TextStyle(color: attrColor),
    'subst': TextStyle(color: c.accentHover),
    'meta': TextStyle(color: c.accentHover),
    'doctag': TextStyle(color: keywordColor),
    'comment': TextStyle(color: c.textMuted, fontStyle: FontStyle.italic),
    'quote': TextStyle(color: c.textMuted, fontStyle: FontStyle.italic),
    'emphasis': const TextStyle(fontStyle: FontStyle.italic),
    'strong': const TextStyle(fontWeight: FontWeight.bold),
  };

  return CodeHighlightTheme(languages: languages, theme: styles);
}

class _SpanCountRenderer implements HighlightRenderer {
  int count = 0;

  @override
  void addText(String text) {}

  @override
  void openNode(DataNode node) {
    if (node.scope != null && node.scope!.isNotEmpty) {
      count++;
    }
  }

  @override
  void closeNode(DataNode node) {}
}

int _countHighlightSpans(String? resolvedSyntax, String content) {
  if (resolvedSyntax == null || content.isEmpty || content.length > 65536) {
    return 0;
  }
  final hl = Highlight();
  final HighlightResult result;
  if (resolvedSyntax == 'auto') {
    hl.registerLanguages(supportedCodeHighlightModes);
    result = hl.highlightAuto(
      content,
      supportedCodeHighlightModes.keys.toList(),
    );
  } else {
    final mode = supportedCodeHighlightModes[resolvedSyntax];
    if (mode == null) return 0;
    hl.registerLanguage(resolvedSyntax, mode);
    result = hl.highlight(code: content, language: resolvedSyntax);
  }
  final counter = _SpanCountRenderer();
  result.render(counter);
  return counter.count;
}

class CodeEditorNeuronWidget extends StatefulWidget {
  const CodeEditorNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  State<CodeEditorNeuronWidget> createState() => _CodeEditorNeuronWidgetState();
}

class _CodeEditorNeuronWidgetState extends State<CodeEditorNeuronWidget> {
  final CodeLineEditingController _controller = CodeLineEditingController();
  int _syncedVersion = -1;
  Timer? _debounce;

  @override
  void dispose() {
    _debounce?.cancel();
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.code-editor',
      neuronKey: widget.neuronKey,
      callNeuron: widget.callNeuron,
      watch: widget.watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final version = (state['version'] as num?)?.toInt() ?? 0;
        final title = (state['title'] as String?) ?? 'untitled';
        final language = (state['language'] as String?) ?? 'text';
        final content = (state['content'] as String?) ?? '';
        final readOnly = state['readOnly'] == true;
        final wordWrap = state['wordWrap'] == true;
        final dirty = state['dirty'] == true;

        if (version != _syncedVersion && _controller.text != content) {
          _syncedVersion = version;
          _controller.text = content;
        }

        final resolvedSyntax = resolveCodeHighlightLanguage(
          language,
          title: title,
        );
        final highlightTheme = buildIcCodeHighlightTheme(
          c,
          language,
          title: title,
        );
        final spanCount = _countHighlightSpans(resolvedSyntax, content);
        final highlightKey = resolvedSyntax ?? 'plain';

        final editorBoxDecoration = BoxDecoration(
          color: c.surfaceSunk,
          borderRadius: BorderRadius.circular(IcRadius.sm),
          border: Border.all(color: c.borderHairline),
        );

        final editorCore = CodeEditor(
          controller: _controller,
          readOnly: readOnly,
          wordWrap: wordWrap,
          style: CodeEditorStyle(
            fontFamily: 'JetBrains Mono',
            fontFamilyFallback: const ['monospace'],
            fontSize: 12.5,
            fontHeight: 1.45,
            textColor: c.textStrong,
            backgroundColor: c.surfaceSunk,
            cursorColor: c.accent,
            selectionColor: c.accent.withValues(alpha: 0.22),
            codeTheme: highlightTheme,
          ),
          indicatorBuilder:
              (context, editingController, chunkController, notifier) {
                return Row(
                  children: [
                    DefaultCodeLineNumber(
                      controller: editingController,
                      notifier: notifier,
                      textStyle: TextStyle(
                        fontFamily: 'JetBrains Mono',
                        fontSize: 11,
                        color: c.textMuted,
                      ),
                    ),
                    const SizedBox(width: 8),
                  ],
                );
              },
          onChanged: (value) {
            if (readOnly) return;
            final text = _controller.text;
            _debounce?.cancel();
            _debounce = Timer(const Duration(milliseconds: 220), () {
              unawaited(invoke('SetContent', [text]));
            });
          },
        );

        return LayoutBuilder(
          builder: (context, constraints) {
            final stretch =
                constraints.hasBoundedHeight &&
                constraints.maxHeight.isFinite &&
                constraints.maxHeight >= 160;
            return Container(
              padding: const EdgeInsets.all(10),
              decoration: BoxDecoration(
                color: c.surfaceCard,
                borderRadius: BorderRadius.circular(IcRadius.card),
                border: Border.all(color: c.borderHairline),
              ),
              child: Column(
                mainAxisSize: stretch ? MainAxisSize.max : MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Row(
                    children: [
                      Icon(Icons.code, size: 15, color: c.accent),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(
                          dirty ? '$title •' : title,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: TextStyle(
                            fontSize: 12.5,
                            fontWeight: FontWeight.w600,
                            color: c.textStrong,
                          ),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Semantics(
                        identifier: 'key:code-highlight:$highlightKey',
                        label:
                            'Syntax highlight: $highlightKey ($spanCount spans)',
                        container: true,
                        child: Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 6,
                            vertical: 2,
                          ),
                          decoration: BoxDecoration(
                            color: resolvedSyntax != null
                                ? c.accentTint
                                : c.surfaceSunk,
                            borderRadius: BorderRadius.circular(4),
                          ),
                          child: Text(
                            highlightKey.toUpperCase(),
                            style: TextStyle(
                              fontSize: 10,
                              fontWeight: FontWeight.w600,
                              color: resolvedSyntax != null
                                  ? c.accent
                                  : c.textMuted,
                            ),
                          ),
                        ),
                      ),
                      if (!readOnly) ...[
                        const SizedBox(width: 8),
                        TextButton.icon(
                          onPressed: () {
                            _debounce?.cancel();
                            unawaited(invoke('SaveContent', const []));
                          },
                          icon: const Icon(Icons.save_outlined, size: 14),
                          label: const Text(
                            'Save',
                            style: TextStyle(fontSize: 12),
                          ),
                        ),
                      ],
                    ],
                  ),
                  const SizedBox(height: 8),
                  if (stretch)
                    Expanded(
                      child: Container(
                        padding: const EdgeInsets.all(8),
                        decoration: editorBoxDecoration,
                        child: editorCore,
                      ),
                    )
                  else
                    Container(
                      height: 200,
                      padding: const EdgeInsets.all(8),
                      decoration: editorBoxDecoration,
                      child: editorCore,
                    ),
                ],
              ),
            );
          },
        );
      },
    );
  }
}
