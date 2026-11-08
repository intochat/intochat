part of '../neuron_view.dart';

class _InoSessionNavItem {
  const _InoSessionNavItem({
    required this.key,
    required this.label,
    required this.icon,
    required this.selected,
    this.badge,
    this.onTap,
  });

  final String key;
  final String label;
  final IconData icon;
  final bool selected;
  final String? badge;
  final VoidCallback? onTap;
}

class _InoParsedCard {
  const _InoParsedCard({
    required this.key,
    required this.kind,
    required this.status,
    required this.title,
    required this.isWaiting,
    required this.widget,
    this.sourceUri,
  });

  final String key;
  final String kind;
  final String status;
  final String title;
  final String? sourceUri;
  final bool isWaiting;
  final Widget widget;
}

class _InoParsedTurn {
  const _InoParsedTurn({
    required this.userMessage,
    required this.reply,
    required this.routeHint,
    this.recalledBadge,
  });

  final String userMessage;
  final String reply;
  final String routeHint;
  final String? recalledBadge;

  (String?, String?) get attachedResource {
    final match = RegExp(
      r'Explain and summarize\s+([^\(\n]+?)\s*\((neuron://[^\)\s]+)\)',
      caseSensitive: false,
    ).firstMatch(userMessage);
    if (match != null) {
      return (match.group(1)!.trim(), match.group(2)!.trim());
    }
    final atMatch = RegExp(r'^@([^\s]+)\s+\((neuron://[^\)\s]+)\)')
        .firstMatch(userMessage);
    if (atMatch != null) {
      return (atMatch.group(1)!.trim(), atMatch.group(2)!.trim());
    }
    return (null, null);
  }

  String get cleanUserMessage {
    final idx = userMessage.indexOf('\n\n[File Preview]\n');
    return idx >= 0 ? userMessage.substring(0, idx).trim() : userMessage;
  }

  String? get routedAppId {
    if (routeHint == 'postgres:query' || routeHint == 'chart:qa') {
      return 'data';
    }
    if (routeHint.startsWith('routed:')) {
      final rest = routeHint.substring('routed:'.length);
      final dot = rest.indexOf('.');
      final app = dot > 0 ? rest.substring(0, dot) : rest;
      if (const {
        'markdraw',
        'data',
        'photocraft',
        'filmcraft',
        'settings',
        'files',
      }.contains(app)) {
        return app;
      }
    }
    return null;
  }

  String get routedAppLabel => switch (routedAppId) {
    'markdraw' => 'Markdraw Studio',
    'data' => 'Data Studio',
    'photocraft' => 'PhotoCraft Studio',
    'filmcraft' => 'FilmCraft Studio',
    'settings' => 'Settings',
    'files' => 'Files',
    _ => 'App',
  };
}

class _InoChatDesktopWorkspace extends StatefulWidget {
  const _InoChatDesktopWorkspace({
    required this.activeConversationId,
    required this.sessionTitle,
    required this.statusText,
    required this.isListening,
    required this.connectedDb,
    required this.pinnedSessions,
    required this.todaySessions,
    required this.cards,
    required this.turns,
    required this.markdown,
    required this.onSubmitPrompt,
    required this.onOpenApp,
    this.onNewSession,
    this.onConfirmWaiting,
    this.onDismissWaiting,
  });

  final String activeConversationId;
  final String sessionTitle;
  final String statusText;
  final bool isListening;
  final String? connectedDb;
  final List<_InoSessionNavItem> pinnedSessions;
  final List<_InoSessionNavItem> todaySessions;
  final List<_InoParsedCard> cards;
  final List<_InoParsedTurn> turns;
  final Widget Function(String) markdown;
  final ValueChanged<String> onSubmitPrompt;
  final ValueChanged<String> onOpenApp;
  final VoidCallback? onNewSession;
  final VoidCallback? onConfirmWaiting;
  final VoidCallback? onDismissWaiting;

  @override
  State<_InoChatDesktopWorkspace> createState() =>
      _InoChatDesktopWorkspaceState();
}

class _InoChatDesktopWorkspaceState extends State<_InoChatDesktopWorkspace> {
  final TextEditingController _controller = TextEditingController();
  final FocusNode _focusNode = FocusNode();
  bool _sidebarOpen = true;
  bool _showSlashMenu = false;
  String? _pendingUserPrompt;
  String? _pendingStageLabel;
  int _turnsAtSubmit = 0;
  int _cardsAtSubmit = 0;
  String _convIdAtSubmit = '';
  Timer? _thinkingTimeout;

  bool get _isBackendThinking =>
      widget.statusText.startsWith('Thinking') ||
      widget.statusText.startsWith('Processing');

  bool get _isThinking => _pendingUserPrompt != null || _isBackendThinking;

  String get _effectiveStatusText => _pendingStageLabel ?? widget.statusText;

  static const List<(String cmd, String title, String subtitle, IconData icon)>
  _slashCommands = [
    (
      '/new',
      'New Conversation',
      'Start a clean Ino session',
      LucideIcons.squarePen,
    ),
    (
      '/clear',
      'Clear Session',
      'Clear turns and cards in active session',
      LucideIcons.trash2,
    ),
    (
      '/pin',
      'Pin / Unpin Session',
      'Toggle pin state in the sidebar',
      LucideIcons.pin,
    ),
    (
      'SELECT opportunity, account, stage, amount FROM opportunities',
      'Query Workspace SQL',
      'Renders live DataTable + Chart from connected database',
      LucideIcons.chartColumn,
    ),
  ];

  @override
  void didUpdateWidget(_InoChatDesktopWorkspace oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (_pendingUserPrompt != null) {
      final turnAdded = widget.turns.length != _turnsAtSubmit;
      final cardAdded = widget.cards.length != _cardsAtSubmit;
      final convChanged = widget.activeConversationId != _convIdAtSubmit;
      final matchedLastTurn =
          widget.turns.isNotEmpty &&
          widget.turns.last.userMessage == _pendingUserPrompt;
      if (turnAdded || cardAdded || convChanged || matchedLastTurn) {
        _thinkingTimeout?.cancel();
        _pendingUserPrompt = null;
        _pendingStageLabel = null;
      }
    }
  }

  @override
  void dispose() {
    _thinkingTimeout?.cancel();
    _controller.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  void _submitWithThinking(String payload) {
    final trimmed = payload.trim();
    if (trimmed.isEmpty) return;
    _thinkingTimeout?.cancel();
    setState(() {
      if (!trimmed.startsWith('/clear') &&
          !trimmed.startsWith('/new') &&
          !trimmed.startsWith('/pin')) {
        _pendingUserPrompt = trimmed;
        _pendingStageLabel = 'Thinking · Processing request…';
        _turnsAtSubmit = widget.turns.length;
        _cardsAtSubmit = widget.cards.length;
        _convIdAtSubmit = widget.activeConversationId;
      } else {
        _pendingUserPrompt = null;
        _pendingStageLabel = null;
      }
    });
    if (_pendingUserPrompt != null) {
      _thinkingTimeout = Timer(const Duration(seconds: 25), () {
        if (mounted && _pendingUserPrompt != null) {
          setState(() {
            _pendingUserPrompt = null;
            _pendingStageLabel = null;
          });
        }
      });
    }
    widget.onSubmitPrompt(trimmed);
  }

  void _sendCurrentText([String? overrideText]) {
    final raw = (overrideText ?? _controller.text).trim();
    if (raw.isEmpty) return;
    setState(() {
      if (overrideText == null) _controller.clear();
      _showSlashMenu = false;
    });
    _submitWithThinking(raw);
  }

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final waitingCard = widget.cards
        .where((card) => card.isWaiting)
        .firstOrNull;

    return LayoutBuilder(
      builder: (context, constraints) {
        final canShowSidebar = constraints.maxWidth >= 680 && _sidebarOpen;
        return Container(
          color: c.surfaceCard,
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (canShowSidebar) _buildArcSidebar(c, waitingCard),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    _buildConversationHeader(
                      c,
                      waitingCard: waitingCard,
                      showSidebarToggle: !canShowSidebar,
                    ),
                    Expanded(
                      child:
                          widget.turns.isEmpty &&
                              widget.cards.isEmpty &&
                              _pendingUserPrompt == null
                          ? _buildEmptyState(c)
                          : _buildConversationStream(c),
                    ),
                    _buildBottomComposer(c, waitingCard),
                  ],
                ),
              ),
            ],
          ),
        );
      },
    );
  }

  Widget _buildArcSidebar(IcColors c, _InoParsedCard? waitingCard) {
    return Container(
      width: 248,
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        border: Border(right: BorderSide(color: c.borderHairline)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(10, 10, 10, 8),
            child: Row(
              children: [
                Expanded(
                  child: _NewSessionInlineBtn(onTap: widget.onNewSession),
                ),
                const SizedBox(width: 6),
                _IconBtn(
                  icon: LucideIcons.panelLeftClose,
                  tooltip: 'Hide sidebar',
                  onTap: () => setState(() => _sidebarOpen = false),
                ),
              ],
            ),
          ),
          Expanded(
            child: ListView(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
              children: [
                if (widget.pinnedSessions.isNotEmpty) ...[
                  const _SidebarSectionTitle(label: 'Pinned'),
                  for (final item in widget.pinnedSessions)
                    _SidebarSessionRow(item: item, onTap: item.onTap),
                  Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 8,
                    ),
                    child: Divider(height: 1, color: c.borderHairline),
                  ),
                ],
                _SidebarSectionTitle(
                  label: 'Today',
                  trailingLabel: widget.turns.isNotEmpty ? 'Clear' : null,
                  onTrailingTap: widget.turns.isNotEmpty
                      ? () => widget.onSubmitPrompt('/clear')
                      : null,
                ),
                _SidebarSessionRow(
                  item: _InoSessionNavItem(
                    key: widget.activeConversationId,
                    label: widget.sessionTitle,
                    icon: LucideIcons.messageSquare,
                    selected: true,
                    badge: waitingCard != null
                        ? 'Needs you'
                        : (widget.turns.isNotEmpty
                              ? '${widget.turns.length}'
                              : null),
                  ),
                  onTap: () {},
                ),
                for (final item in widget.todaySessions)
                  _SidebarSessionRow(item: item, onTap: item.onTap),
              ],
            ),
          ),
          Container(
            height: 38,
            padding: const EdgeInsets.symmetric(horizontal: 12),
            decoration: BoxDecoration(
              border: Border(top: BorderSide(color: c.borderHairline)),
            ),
            child: Row(
              children: [
                if (_isThinking)
                  SizedBox(
                    width: 10,
                    height: 10,
                    child: CircularProgressIndicator(
                      strokeWidth: 1.6,
                      color: c.working,
                    ),
                  )
                else
                  Container(
                    width: 7,
                    height: 7,
                    decoration: BoxDecoration(
                      color: widget.isListening ? c.working : c.done,
                      shape: BoxShape.circle,
                    ),
                  ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    _effectiveStatusText,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: IcType.text(
                      11.5,
                      weight: _isThinking ? FontWeight.w600 : FontWeight.w400,
                      color: _isThinking ? c.working : c.textMuted,
                      tnum: true,
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

  Widget _buildConversationHeader(
    IcColors c, {
    required _InoParsedCard? waitingCard,
    required bool showSidebarToggle,
  }) {
    return Container(
      height: 52,
      padding: const EdgeInsets.symmetric(horizontal: 16),
      decoration: BoxDecoration(
        border: Border(bottom: BorderSide(color: c.borderHairline)),
      ),
      child: Row(
        children: [
          if (showSidebarToggle) ...[
            _IconBtn(
              icon: LucideIcons.panelLeft,
              tooltip: 'Show sidebar',
              onTap: () => setState(() => _sidebarOpen = true),
            ),
            const SizedBox(width: 8),
          ],
          Icon(LucideIcons.sparkles, size: 15, color: c.accent),
          const SizedBox(width: 8),
          Flexible(
            child: Text(
              widget.sessionTitle,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: IcType.text(
                14,
                weight: FontWeight.w600,
                color: c.textStrong,
              ),
            ),
          ),
          if (_isThinking) ...[
            const SizedBox(width: 10),
            TagPill(
              label: _effectiveStatusText,
              tone: TagTone.working,
              icon: LucideIcons.loader,
            ),
          ] else if (waitingCard != null) ...[
            const SizedBox(width: 10),
            TagPill(
              label: 'Needs your response',
              tone: TagTone.accent,
              icon: LucideIcons.hand,
            ),
          ],
          if (widget.connectedDb != null) ...[
            const SizedBox(width: 8),
            TagPill(
              label: widget.connectedDb!,
              tone: TagTone.accent,
              icon: LucideIcons.database,
            ),
          ],
          const Spacer(),
          _IconBtn(
            icon: LucideIcons.pin,
            tooltip: 'Pin session',
            onTap: () => widget.onSubmitPrompt('/pin'),
          ),
          const SizedBox(width: 4),
          _IconBtn(
            icon: LucideIcons.plus,
            tooltip: 'New session',
            onTap: widget.onNewSession,
          ),
          const SizedBox(width: 4),
          _IconBtn(
            icon: LucideIcons.slidersHorizontal,
            tooltip: 'AI & Model Settings',
            onTap: () => widget.onOpenApp('settings'),
          ),
        ],
      ),
    );
  }

  Widget _buildEmptyState(IcColors c) {
    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.symmetric(horizontal: 28, vertical: 24),
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 520),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 48,
                height: 48,
                decoration: BoxDecoration(
                  color: c.accentTint,
                  borderRadius: BorderRadius.circular(14),
                  border: Border.all(color: c.accent.withValues(alpha: 0.25)),
                ),
                child: Icon(LucideIcons.sparkles, size: 22, color: c.accent),
              ),
              const SizedBox(height: 16),
              Text(
                'What can I do for you?',
                style: IcType.text(
                  20,
                  weight: FontWeight.w600,
                  color: c.textStrong,
                  letterSpacing: -0.3,
                ),
              ),
              const SizedBox(height: 6),
              Text(
                'Talk to your brain, query connected databases, or manage files and studio apps.',
                textAlign: TextAlign.center,
                style: IcType.text(13, color: c.textMuted, height: 1.4),
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildConversationStream(IcColors c) {
    final hasTurns = widget.turns.isNotEmpty;
    final pendingPrompt = _pendingUserPrompt;
    return ListView(
      padding: const EdgeInsets.fromLTRB(24, 18, 24, 20),
      children: [
        Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 760),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: 18,
              children: [
                for (var i = 0; i < widget.turns.length; i++) ...[
                  _buildUserTurnBubble(c, widget.turns[i]),
                  _buildAssistantTurnBlock(
                    c,
                    widget.turns[i],
                    inlineCards: i == widget.turns.length - 1
                        ? widget.cards
                        : const <_InoParsedCard>[],
                  ),
                ],
                if (!hasTurns)
                  for (final card in widget.cards)
                    _buildInlineCardMessage(c, card),
                if (pendingPrompt != null) ...[
                  _buildUserTurnBubble(
                    c,
                    _InoParsedTurn(
                      userMessage: pendingPrompt,
                      reply: '',
                      routeHint: 'working',
                    ),
                  ),
                  _buildThinkingTurnBlock(
                    c,
                    _pendingStageLabel ?? 'Thinking · Processing request…',
                  ),
                ] else if (_isBackendThinking)
                  _buildThinkingTurnBlock(c, widget.statusText),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildThinkingTurnBlock(IcColors c, String stageLabel) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          width: 26,
          height: 26,
          margin: const EdgeInsets.only(top: 2),
          decoration: BoxDecoration(
            color: c.accentTint,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: c.accent.withValues(alpha: 0.35)),
          ),
          alignment: Alignment.center,
          child: SizedBox(
            width: 13,
            height: 13,
            child: CircularProgressIndicator(strokeWidth: 1.8, color: c.accent),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
            decoration: BoxDecoration(
              color: c.surfaceSunk,
              borderRadius: BorderRadius.circular(IcRadius.card),
              border: Border.all(color: c.borderHairline),
            ),
            child: Row(
              children: [
                Icon(LucideIcons.sparkles, size: 14, color: c.working),
                const SizedBox(width: 8),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Ino is thinking…',
                        style: IcType.text(
                          13,
                          weight: FontWeight.w600,
                          color: c.textStrong,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        stageLabel,
                        style: IcType.mono(11.5, color: c.textMuted),
                      ),
                    ],
                  ),
                ),
                const TagPill(label: 'Working', tone: TagTone.working),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildInlineCardMessage(IcColors c, _InoParsedCard card) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          width: 26,
          height: 26,
          margin: const EdgeInsets.only(top: 2),
          decoration: BoxDecoration(
            color: c.accentTint,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: c.accent.withValues(alpha: 0.25)),
          ),
          child: Icon(LucideIcons.sparkles, size: 13, color: c.accent),
        ),
        const SizedBox(width: 12),
        Expanded(child: card.widget),
      ],
    );
  }

  Widget _buildUserTurnBubble(IcColors c, _InoParsedTurn turn) {
    final (resName, resUri) = turn.attachedResource;
    return Align(
      alignment: Alignment.centerRight,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 540),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            if (resName != null && resUri != null)
              Container(
                margin: const EdgeInsets.only(bottom: 6),
                padding: const EdgeInsets.symmetric(
                  horizontal: 10,
                  vertical: 5,
                ),
                decoration: BoxDecoration(
                  color: c.surfaceSunk,
                  borderRadius: BorderRadius.circular(IcRadius.sm),
                  border: Border.all(color: c.borderInput),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(LucideIcons.fileText, size: 13, color: c.accent),
                    const SizedBox(width: 6),
                    Flexible(
                      child: Text(
                        resName,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: IcType.text(
                          12,
                          weight: FontWeight.w600,
                          color: c.textStrong,
                        ),
                      ),
                    ),
                    const SizedBox(width: 6),
                    Flexible(
                      child: Text(
                        resUri,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: IcType.mono(10.5, color: c.textMuted),
                      ),
                    ),
                  ],
                ),
              ),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
              decoration: BoxDecoration(
                color: c.accentTint,
                borderRadius: BorderRadius.circular(14),
                border: Border.all(color: c.accent.withValues(alpha: 0.25)),
              ),
              child: SelectableText(
                turn.cleanUserMessage,
                style: IcType.text(13.5, color: c.textStrong, height: 1.45),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildAssistantTurnBlock(
    IcColors c,
    _InoParsedTurn turn, {
    List<_InoParsedCard> inlineCards = const [],
  }) {
    final targetApp = turn.routedAppId;
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          width: 26,
          height: 26,
          margin: const EdgeInsets.only(top: 2),
          decoration: BoxDecoration(
            color: c.accentTint,
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: c.accent.withValues(alpha: 0.25)),
          ),
          child: Icon(LucideIcons.sparkles, size: 13, color: c.accent),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              widget.markdown(turn.reply),
              if (inlineCards.isNotEmpty) ...[
                const SizedBox(height: 12),
                for (var i = 0; i < inlineCards.length; i++) ...[
                  if (i > 0) const SizedBox(height: 12),
                  inlineCards[i].widget,
                ],
              ],
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                runSpacing: 6,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  Text(
                    turn.routeHint,
                    style: IcType.mono(11, color: c.textMuted),
                  ),
                  if (turn.recalledBadge != null &&
                      turn.recalledBadge!.isNotEmpty)
                    TagPill(
                      label: turn.recalledBadge!,
                      tone: TagTone.done,
                      icon: LucideIcons.brain,
                    ),
                  GestureDetector(
                    onTap: () {
                      Clipboard.setData(ClipboardData(text: turn.reply));
                      showKitToast(context, 'Copied reply');
                    },
                    child: MouseRegion(
                      cursor: SystemMouseCursors.click,
                      child: Icon(
                        LucideIcons.copy,
                        size: 12,
                        color: c.textMuted,
                      ),
                    ),
                  ),
                  if (targetApp != null)
                    Btn(
                      label: 'Open ${turn.routedAppLabel}',
                      icon: LucideIcons.externalLink,
                      small: true,
                      onTap: () => widget.onOpenApp(targetApp),
                    ),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }

  Widget _buildBottomComposer(IcColors c, _InoParsedCard? waitingCard) {
    return Container(
      padding: const EdgeInsets.fromLTRB(20, 8, 20, 14),
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 760),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (_showSlashMenu) _buildSlashPickerPopover(c),
              Container(
                padding: const EdgeInsets.fromLTRB(10, 6, 8, 6),
                decoration: BoxDecoration(
                  color: c.surfaceSunk,
                  borderRadius: BorderRadius.circular(14),
                  border: Border.all(color: c.borderInput),
                ),
                child: Row(
                  children: [
                    _IconBtn(
                      icon: LucideIcons.folderSearch,
                      tooltip: 'Browse files in Files app',
                      onTap: () => widget.onOpenApp('files'),
                    ),
                    const SizedBox(width: 4),
                    _IconBtn(
                      icon: LucideIcons.slash,
                      tooltip: 'Commands (/)',
                      active: _showSlashMenu,
                      onTap: () => setState(() {
                        _showSlashMenu = !_showSlashMenu;
                      }),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: KeyboardListener(
                        focusNode: FocusNode(),
                        onKeyEvent: (event) {
                          if (event is KeyDownEvent &&
                              event.logicalKey == LogicalKeyboardKey.enter &&
                              !HardwareKeyboard.instance.isShiftPressed) {
                            if (_controller.text.trim().isEmpty &&
                                waitingCard != null &&
                                widget.onConfirmWaiting != null) {
                              widget.onConfirmWaiting!();
                            }
                          } else if (event is KeyDownEvent &&
                              event.logicalKey == LogicalKeyboardKey.escape &&
                              waitingCard != null &&
                              widget.onDismissWaiting != null) {
                            widget.onDismissWaiting!();
                          }
                        },
                        child: TextField(
                          key: const ValueKey('ino-composer-input'),
                          controller: _controller,
                          focusNode: _focusNode,
                          minLines: 1,
                          maxLines: 4,
                          textInputAction: TextInputAction.send,
                          onSubmitted: (_) => _sendCurrentText(),
                          onChanged: (val) {
                            if (val == '/') {
                              setState(() => _showSlashMenu = true);
                            }
                          },
                          style: IcType.text(13.5, color: c.textStrong),
                          decoration: InputDecoration(
                            isDense: true,
                            border: InputBorder.none,
                            hintText: 'Message Ino or type / for commands…',
                            hintStyle: IcType.text(13.5, color: c.textMuted),
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(width: 6),
                    GestureDetector(
                      key: const ValueKey('ino-composer-send'),
                      onTap: _sendCurrentText,
                      child: MouseRegion(
                        cursor: SystemMouseCursors.click,
                        child: Container(
                          width: 30,
                          height: 30,
                          alignment: Alignment.center,
                          decoration: BoxDecoration(
                            color: _isThinking ? c.working : c.accent,
                            borderRadius: BorderRadius.circular(9),
                          ),
                          child: _isThinking
                              ? const SizedBox(
                                  width: 14,
                                  height: 14,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 1.8,
                                    color: Colors.white,
                                  ),
                                )
                              : const Icon(
                                  LucideIcons.arrowUp,
                                  size: 15,
                                  color: Colors.white,
                                ),
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

  Widget _buildSlashPickerPopover(IcColors c) {
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        color: c.surfaceCard,
        borderRadius: BorderRadius.circular(IcRadius.card),
        border: Border.all(color: c.borderInput),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(8, 4, 8, 6),
            child: Text(
              'Commands',
              style: IcType.text(
                11.5,
                weight: FontWeight.w600,
                color: c.textMuted,
              ),
            ),
          ),
          for (final (cmd, title, sub, icon) in _slashCommands)
            GestureDetector(
              onTap: () {
                setState(() {
                  _showSlashMenu = false;
                  if (_controller.text == '/') _controller.clear();
                });
                _submitWithThinking(cmd);
              },
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 10,
                    vertical: 6,
                  ),
                  child: Row(
                    children: [
                      Icon(icon, size: 14, color: c.accent),
                      const SizedBox(width: 8),
                      Text(
                        title,
                        style: IcType.text(
                          12.5,
                          weight: FontWeight.w600,
                          color: c.textStrong,
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          sub,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                          style: IcType.text(11.5, color: c.textMuted),
                        ),
                      ),
                      Text(cmd, style: IcType.mono(10.5, color: c.textMuted)),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _IconBtn extends StatelessWidget {
  const _IconBtn({
    required this.icon,
    required this.tooltip,
    this.onTap,
    this.active = false,
  });

  final IconData icon;
  final String tooltip;
  final VoidCallback? onTap;
  final bool active;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Tooltip(
      message: tooltip,
      child: GestureDetector(
        onTap: onTap,
        child: MouseRegion(
          cursor: onTap != null
              ? SystemMouseCursors.click
              : SystemMouseCursors.basic,
          child: Container(
            width: 28,
            height: 28,
            decoration: BoxDecoration(
              color: active ? c.accentTint : Colors.transparent,
              borderRadius: BorderRadius.circular(IcRadius.xs),
            ),
            child: Icon(icon, size: 14, color: active ? c.accent : c.textMuted),
          ),
        ),
      ),
    );
  }
}

class _SidebarSectionTitle extends StatelessWidget {
  const _SidebarSectionTitle({
    required this.label,
    this.trailingLabel,
    this.onTrailingTap,
  });

  final String label;
  final String? trailingLabel;
  final VoidCallback? onTrailingTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 4, 8, 4),
      child: Row(
        children: [
          Text(
            label,
            style: IcType.text(11, weight: FontWeight.w600, color: c.textMuted),
          ),
          const Spacer(),
          if (trailingLabel != null && onTrailingTap != null)
            GestureDetector(
              onTap: onTrailingTap,
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                child: Text(
                  trailingLabel!,
                  style: IcType.text(11, color: c.textMuted),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _SidebarSessionRow extends StatelessWidget {
  const _SidebarSessionRow({required this.item, this.onTap});

  final _InoSessionNavItem item;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    final isWaitingBadge = item.badge == 'Needs you';
    return GestureDetector(
      onTap: onTap,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: Container(
          height: 32,
          margin: const EdgeInsets.symmetric(vertical: 1),
          padding: const EdgeInsets.symmetric(horizontal: 8),
          decoration: BoxDecoration(
            color: item.selected ? c.surfaceElevated : Colors.transparent,
            borderRadius: BorderRadius.circular(IcRadius.sm),
          ),
          child: Row(
            children: [
              Icon(
                item.icon,
                size: 13.5,
                color: item.selected ? c.accent : c.textMuted,
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  item.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: IcType.text(
                    12.5,
                    weight: item.selected ? FontWeight.w600 : FontWeight.w400,
                    color: item.selected ? c.textStrong : c.textBody,
                  ),
                ),
              ),
              if (isWaitingBadge)
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 6,
                    vertical: 2,
                  ),
                  decoration: BoxDecoration(
                    color: c.accentTint,
                    borderRadius: BorderRadius.circular(4),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Icon(LucideIcons.hand, size: 10.5, color: c.accent),
                      const SizedBox(width: 3),
                      Text(
                        'Wait',
                        style: IcType.text(
                          10,
                          weight: FontWeight.w600,
                          color: c.accent,
                        ),
                      ),
                    ],
                  ),
                )
              else if (item.badge != null && item.badge!.isNotEmpty)
                Container(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 5,
                    vertical: 1,
                  ),
                  decoration: BoxDecoration(
                    color: c.surfaceCard,
                    borderRadius: BorderRadius.circular(4),
                    border: Border.all(color: c.borderHairline),
                  ),
                  child: Text(
                    item.badge!,
                    style: IcType.text(
                      10,
                      weight: FontWeight.w600,
                      color: c.textMuted,
                      tnum: true,
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

class _NewSessionInlineBtn extends StatelessWidget {
  const _NewSessionInlineBtn({this.onTap});

  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final c = IcTheme.of(context);
    return GestureDetector(
      onTap: onTap,
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: Container(
          height: 30,
          padding: const EdgeInsets.symmetric(horizontal: 8),
          child: Row(
            children: [
              Icon(LucideIcons.plus, size: 13.5, color: c.textMuted),
              const SizedBox(width: 8),
              Text(
                'New session',
                style: IcType.text(
                  12.5,
                  weight: FontWeight.w500,
                  color: c.textMuted,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
