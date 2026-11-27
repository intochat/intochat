import 'package:flutter/material.dart';
import 'package:gpt_markdown/gpt_markdown.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/browser/ui_web_browser.dart';
import '../components/image/ui_image.dart';
import '../components/json/ui_json_tree.dart';
import '../primitives/display_primitives.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/wire_node_values.dart';
import 'node_common.dart';
import 'node_context.dart';

// --- src/nodes/text.dart ---

Widget renderText(TextNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final theme = Theme.of(context);
    final colors = theme.extension<UiColors>();
    final (toneKind, semanticTone) = parseTextTone(node.tone);
    final color = switch (toneKind) {
      TextToneVariant.muted =>
        colors?.muted ?? theme.colorScheme.onSurfaceVariant,
      TextToneVariant.strong => colors?.strong ?? theme.colorScheme.onSurface,
      TextToneVariant.semantic => switch (semanticTone) {
        UiNodeTone.accent => colors?.accent ?? theme.colorScheme.primary,
        UiNodeTone.done => colors?.done,
        UiNodeTone.working => colors?.working,
        UiNodeTone.error => colors?.error ?? theme.colorScheme.error,
        UiNodeTone.neutral => null,
      },
    };
    final baseStyle = node.mono
        ? UiType.tabularStyle(
            node.size ?? 12,
            weight: FontWeight.w600,
            color: color ?? theme.textTheme.bodyMedium?.color,
          ).copyWith(letterSpacing: -0.1)
        : (theme.textTheme.bodyMedium ?? const TextStyle(fontSize: 13));
    // Selectable text reaches the web accessibility tree as an empty read-only
    // field; its label carries the text a screen reader (or a browser test) reads.
    return SelectableText(
      node.value,
      semanticsLabel: node.value,
      style: baseStyle.copyWith(
        color: color,
        fontWeight: toneKind == TextToneVariant.strong
            ? FontWeight.w600
            : (node.mono ? FontWeight.w600 : null),
        fontSize: node.size,
        fontFeatures: (node.tabular || node.mono) ? UiType.tabular : null,
      ),
    );
  },
);

// --- src/nodes/heading.dart ---

Widget renderHeading(HeadingNode node, NodeContext ctx) => Builder(
  builder: (context) {
    final c = UiColors.of(context);
    final text = Theme.of(context).textTheme;

    // Level 6+ renders a crisp left-aligned uppercase eyebrow SectionHeader
    // (never crushed by hairline dividers inside narrow sidebars).
    if (node.level >= 6) {
      final eyebrowStyle = UiType.textStyle(
        11,
        weight: FontWeight.w700,
        color: c.muted,
        height: 1.3,
      ).copyWith(letterSpacing: 0.75);
      return Semantics(
        header: true,
        child: Padding(
          padding: const EdgeInsets.only(top: 4, bottom: 2),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Flexible(
                child: Text(
                  node.value.toUpperCase(),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: eyebrowStyle,
                ),
              ),
              if (node.subtitle case final sub? when sub.isNotEmpty) ...[
                const SizedBox(width: 8),
                Text(
                  sub,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: text.labelSmall?.copyWith(color: c.subtle),
                ),
              ],
            ],
          ),
        ),
      );
    }

    final style = switch (node.level) {
      <= 1 => UiType.displayStyle(
        26,
        weight: FontWeight.w700,
        color: c.strong,
      ).copyWith(letterSpacing: -0.55, height: 1.14),
      2 => UiType.displayStyle(
        20,
        weight: FontWeight.w700,
        color: c.strong,
      ).copyWith(letterSpacing: -0.40, height: 1.18),
      3 => UiType.displayStyle(
        16,
        weight: FontWeight.w600,
        color: c.strong,
      ).copyWith(letterSpacing: -0.25, height: 1.22),
      4 => UiType.textStyle(
        14,
        weight: FontWeight.w600,
        color: c.strong,
        height: 1.25,
      ).copyWith(letterSpacing: -0.15),
      _ => UiType.textStyle(
        13,
        weight: FontWeight.w600,
        color: c.strong,
        height: 1.30,
      ).copyWith(letterSpacing: -0.10),
    };

    return Semantics(
      header: true,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        spacing: 2,
        children: [
          Text(
            node.value,
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
            style: style,
          ),
          if (node.subtitle case final subtitle? when subtitle.isNotEmpty)
            Text(
              subtitle,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: text.bodySmall?.copyWith(color: c.muted),
            ),
        ],
      ),
    );
  },
);

// --- src/nodes/markdown.dart ---
Widget renderMarkdown(MarkdownNode node, NodeContext ctx) => Builder(
  builder: (context) => GptMarkdown(
    node.value,
    style: Theme.of(context).textTheme.bodyMedium
        ?.copyWith(color: UiColors.of(context).textBody),
  ),
);

// --- src/nodes/badge.dart ---
Widget renderBadge(BadgeNode node, NodeContext ctx) => UiBadge.fromWire(
  label: node.label,
  tone: node.tone,
  icon: node.icon,
  statusBadge: true,
);

// --- src/nodes/tag_pill.dart ---
Widget renderTagPill(TagPillNode node, NodeContext ctx) => UiBadge.fromWire(
  label: node.label,
  tone: nodeToneOf(node.tone),
  icon: node.icon,
);

// --- src/nodes/image.dart ---
Widget renderImage(ImageNode node, NodeContext ctx) => UiImageViewer(
  url: node.url,
  caption: node.caption,
  height: node.height,
  fit: switch (node.fit) {
    ImageFit.contain => BoxFit.contain,
    ImageFit.cover => BoxFit.cover,
  },
);

// --- src/nodes/json_tree.dart ---
Widget renderJsonTree(JsonTreeNode node, NodeContext ctx) =>
    UiJsonTree(title: node.title, json: node.json);

// --- src/nodes/web_browser.dart ---
Widget renderWebBrowser(WebBrowserNode node, NodeContext ctx) => Semantics(
  container: true,
  explicitChildNodes: true,
  label: controlNameOf(node.automationName, node.title ?? node.key, node.key),
  child: LayoutBuilder(
    builder: (context, constraints) {
      final effectiveHeight =
          constraints.hasBoundedHeight && constraints.maxHeight.isFinite
          ? constraints.maxHeight
          : node.height.clamp(160.0, 1600.0);
      return UiWebBrowser(
        key: ValueKey('browser:${node.key}'),
        part: UiWebBrowserPart(
          name: '${ctx.view}#${node.key}',
          uri: node.url,
          title: node.title ?? node.url,
        ),
        height: effectiveHeight,
        chrome: node.chrome,
      );
    },
  ),
);
