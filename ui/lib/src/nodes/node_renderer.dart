import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'app_shell.dart';
import 'approval_card.dart';
import 'badge.dart';
import 'brain_scene.dart';
import 'button.dart';
import 'card.dart';
import 'chart.dart';
import 'choice_card.dart';
import 'collection.dart';
import 'column.dart';
import 'conversation.dart';
import 'data_table.dart';
import 'diff_card.dart';
import 'divider.dart';
import 'draft_card.dart';
import 'empty_state.dart';
import 'error_line.dart';
import 'field.dart';
import 'grid.dart';
import 'heading.dart';
import 'info_bar.dart';
import 'intent.dart';
import 'job_card.dart';
import 'metric_tile.dart';
import 'nav_item.dart';
import 'neuron_ref.dart';
import 'node_context.dart';
import 'progress.dart';
import 'record_card.dart';
import 'row.dart';
import 'secret_card.dart';
import 'select.dart';
import 'spacer.dart';
import 'split_pane.dart';
import 'stat_card.dart';
import 'surface.dart';
import 'switch.dart';
import 'tabs.dart';
import 'tag_pill.dart';
import 'text.dart';
import 'toast.dart';
import 'web_browser.dart';

/// The one renderer: a published `DigitalBrain.UI.View` node tree to widgets.
abstract final class NodeRenderer {
  static const maxDepth = 48;

  /// True for a root node that manages its own vertical scrolling and fills its host box.
  static bool fills(Node root) => switch (root) {
    ConversationNode() => true,
    SplitPaneNode(:final trailing) => fills(trailing),
    RowNode(:final children) => children.any(fills),
    _ => false,
  };

  /// Draws a whole view: its root node.
  static Widget view(AppView view, NodeContext ctx) => render(view.root, ctx);

  static Widget render(Node node, NodeContext ctx) {
    if (ctx.depth > maxDepth) return const _Unsupported();
    return switch (node) {
      ColumnNode() => renderColumn(node, ctx),
      RowNode() => renderRow(node, ctx),
      GridNode() => renderGrid(node, ctx),
      SplitPaneNode() => renderSplitPane(node, ctx),
      SurfaceNode() => renderSurface(node, ctx),
      TabsNode() => renderTabs(node, ctx),
      DividerNode() => renderDivider(node, ctx),
      SpacerNode() => renderSpacer(node, ctx),
      CollectionNode() => renderCollection(node, ctx),
      HeadingNode() => renderHeading(node, ctx),
      TextNode() => renderText(node, ctx),
      BadgeNode() => renderBadge(node, ctx),
      TagPillNode() => renderTagPill(node, ctx),
      FieldNode() => renderField(node, ctx),
      IntentNode() => renderIntent(node, ctx),
      SwitchNode() => renderSwitch(node, ctx),
      SelectNode() => renderSelect(node, ctx),
      ProgressNode() => renderProgress(node, ctx),
      InfoBarNode() => renderInfoBar(node, ctx),
      ToastNode() => renderToast(node, ctx),
      CardNode() => renderCard(node, ctx),
      ButtonNode() => renderButton(node, ctx),
      DataTableNode() => renderDataTable(node, ctx),
      ChartNode() => renderChart(node, ctx),
      ApprovalCardNode() => renderApprovalCard(node, ctx),
      ChoiceCardNode() => renderChoiceCard(node, ctx),
      DraftCardNode() => renderDraftCard(node, ctx),
      RecordCardNode() => renderRecordCard(node, ctx),
      SecretCardNode() => renderSecretCard(node, ctx),
      JobCardNode() => renderJobCard(node, ctx),
      BrainSceneNode() => renderBrainScene(node, ctx),
      NeuronRefNode() => renderNeuronRef(node, ctx),
      AppShellNode() => renderAppShell(node, ctx),
      NavItemNode() => renderNavItem(node, ctx),
      StatCardNode() => renderStatCard(node, ctx),
      WebBrowserNode() => renderWebBrowser(node, ctx),
      ConversationNode() => renderConversation(node, ctx),
      ErrorLineNode() => renderErrorLine(node, ctx),
      EmptyStateNode() => renderEmptyState(node, ctx),
      MetricTileNode() => renderMetricTile(node, ctx),
      DiffCardNode() => renderDiffCard(node, ctx),
      UnknownNode() => const _Unsupported(),
    };
  }
}

class _Unsupported extends StatelessWidget {
  const _Unsupported();
  @override
  Widget build(BuildContext context) =>
      const Text('This component cannot be displayed.');
}
