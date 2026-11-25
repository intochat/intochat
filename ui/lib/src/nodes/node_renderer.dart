import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'app_shell.dart';
import 'app_tile.dart';
import 'approval_card.dart';
import 'badge.dart';
import 'node_common.dart' show keyedControl;
import 'brain_scene.dart';
import 'button.dart';
import 'icon_button.dart';
import 'markdown.dart';
import 'card.dart';
import 'chart.dart';
import 'check_box_view.dart';
import 'expander_view.dart';
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
import 'spacer.dart';
import 'split_pane.dart';
import 'stat_card.dart';
import 'surface.dart';
import 'chat_thread_view.dart';
import 'combo_box_view.dart';
import 'form_view.dart';
import 'list_box_view.dart';
import 'live_chart_view.dart';
import 'query_table_view.dart';
import 'tab_strip_view.dart';
import 'tag_pill.dart';
import 'text.dart';
import 'toggle_switch_view.dart';
import 'toast.dart';
import 'web_browser.dart';
import '../theme/wire_node_values.dart';

/// The one renderer: a published `DigitalBrain.UI.View` node tree to widgets.
abstract final class NodeRenderer {
  static const maxDepth = 48;

  /// True for a root node that manages its own vertical scrolling and fills its host box.
  static bool fills(Node root) => switch (root) {
    ConversationNode() => true,
    ChatThreadViewNode() => true,
    SplitPaneNode() => true,
    AppShellNode() => true,
    BrainSceneNode() => true,
    WebBrowserNode() => true,
    NeuronRefNode(:final refKind) => isFillingNeuronRefKind(refKind),
    CollectionNode(:final entries) =>
      entries.length >= kVirtualizedCollectionThreshold,
    RowNode(:final children) => children.any(fills),
    ColumnNode(:final children, :final extents) =>
      (extents != null && extents.any((e) => e > 0 && e <= 1)) ||
          (children.length <= 4 && children.where(fills).length == 1),
    SurfaceNode(:final surfaceKind, :final children) =>
      children.length == 1 &&
          isContainerSurfaceFillable(surfaceKind) &&
          fills(children.single),
    CardNode(:final children) => children.length == 1 && fills(children.single),
    _ => false,
  };

  /// Draws a whole view: its root node.
  static Widget view(AppView view, NodeContext ctx) => render(view.root, ctx);

  /// Test hook: returns a widget to draw in place of a node, or null to draw
  /// it as usual (the scenario driver's bite test blanks one node kind).
  @visibleForTesting
  static Widget? Function(Node node)? debugReplace;

  static Widget render(Node node, NodeContext ctx) {
    if (ctx.depth > maxDepth) return const _Unsupported();
    return keyedControl(
      keyOf(node),
      debugReplace?.call(node) ?? _draw(node, ctx),
    );
  }

  /// The wire `key` of the nodes whose generated model declares one (every such
  /// model is covered; `showcase_catalog_test.dart` fails when a new one is not).
  static String? keyOf(Node node) => switch (node) {
    AppShellNode(:final key) ||
    AppTileNode(:final key) ||
    ApprovalCardNode(:final key) ||
    ButtonNode(:final key) ||
    CheckBoxViewNode(:final key) ||
    ExpanderViewNode(:final key) ||
    TabStripViewNode(:final key) ||
    ComboBoxViewNode(:final key) ||
    FormViewNode(:final key) ||
    ListBoxViewNode(:final key) ||
    LiveChartViewNode(:final key) ||
    ChatThreadViewNode(:final key) ||
    QueryTableViewNode(:final key) ||
    ConversationNode(:final key) ||
    FieldNode(:final key) ||
    IconButtonNode(:final key) ||
    InfoBarNode(:final key) ||
    NavItemNode(:final key) ||
    StatCardNode(:final key) ||
    ToggleSwitchViewNode(:final key) ||
    WebBrowserNode(:final key) => key,
    _ => null,
  };

  static Widget _draw(Node node, NodeContext ctx) {
    return switch (node) {
      ColumnNode() => renderColumn(node, ctx),
      RowNode() => renderRow(node, ctx),
      GridNode() => renderGrid(node, ctx),
      SplitPaneNode() => renderSplitPane(node, ctx),
      SurfaceNode() => renderSurface(node, ctx),
      DividerNode() => renderDivider(node, ctx),
      SpacerNode() => renderSpacer(node, ctx),
      CollectionNode() => renderCollection(node, ctx),
      HeadingNode() => renderHeading(node, ctx),
      TextNode() => renderText(node, ctx),
      MarkdownNode() => renderMarkdown(node, ctx),
      BadgeNode() => renderBadge(node, ctx),
      TagPillNode() => renderTagPill(node, ctx),
      FieldNode() => renderField(node, ctx),
      IntentNode() => renderIntent(node, ctx),
      ProgressNode() => renderProgress(node, ctx),
      InfoBarNode() => renderInfoBar(node, ctx),
      ToastNode() => renderToast(node, ctx),
      CardNode() => renderCard(node, ctx),
      ButtonNode() => renderButton(node, ctx),
      IconButtonNode() => renderIconButton(node, ctx),
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
      AppTileNode() => renderAppTile(node, ctx),
      DiffCardNode() => renderDiffCard(node, ctx),
      CheckBoxViewNode() => renderCheckBoxView(node, ctx),
      ToggleSwitchViewNode() => renderToggleSwitchView(node, ctx),
      ExpanderViewNode() => renderExpanderView(node, ctx),
      TabStripViewNode() => renderTabStripView(node, ctx),
      ComboBoxViewNode() => renderComboBoxView(node, ctx),
      FormViewNode() => renderFormView(node, ctx),
      ListBoxViewNode() => renderListBoxView(node, ctx),
      LiveChartViewNode() => renderLiveChartView(node, ctx),
      ChatThreadViewNode() => renderChatThreadView(node, ctx),
      QueryTableViewNode() => renderQueryTableView(node, ctx),
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
