part of '../neuron_view.dart';

extension _UiKitInoWorkspaceAdapter on ImmutableView {
  Future<void> _submitToInoComposer(String rawText) async {
    final trimmed = rawText.trim();
    if (trimmed.isEmpty || !enabled) return;
    final appsIdx = name.indexOf('/apps/');
    final brainPrefix = appsIdx > 0 ? name.substring(0, appsIdx) : 'owner';
    final appKey = '$brainPrefix/apps/ino';
    final composerName = '$appKey/composer';
    try {
      await dispatch({
        'kind': 'composer',
        'name': composerName,
        'action': 'edit',
        'context': appKey,
        'selectionRevision': 1,
        'value': trimmed,
      });
      await dispatch({
        'kind': 'composer',
        'name': composerName,
        'action': 'submit',
        'context': appKey,
        'selectionRevision': 1,
        'value': trimmed,
        'operationId': ImmutableView._operationId(),
      });
    } catch (_) {}
  }

  Widget _buildInoChatDesktopView(
    BuildContext context,
    Map node,
    List<Map> children,
    int depth,
  ) {
    Map? sideNode;
    Map? mainNode;
    final rootKey = node['key'] as String? ?? '';
    if (rootKey == 'ino-workspace') {
      for (final c in children) {
        final k = c['key'] as String? ?? '';
        if (k == 'ino-side' || k == 'side') sideNode = c;
        if (k == 'ino-main' || k == 'root') mainNode = c;
      }
    } else {
      mainNode = node;
    }

    VoidCallback? onNewSession;
    final pinnedSessions = <_InoSessionNavItem>[];
    final todaySessions = <_InoSessionNavItem>[];
    var currentGroup = 'Pinned';
    var activeConversationId = 'main';

    if (sideNode != null) {
      for (final sc
          in (sideNode['children'] as List? ?? const []).whereType<Map>()) {
        final sk = sc['key'] as String? ?? '';
        if (sc['kind'] == 4 && sk.endsWith('.0')) {
          for (final sub
              in (sc['children'] as List? ?? const []).whereType<Map>()) {
            if (sub['kind'] == 1 && (sub['action'] as String? ?? '') == 'new') {
              onNewSession = _dispatchButton(context, sub);
            }
          }
        } else if (sc['kind'] == 12) {
          final divLabel = (sc['text'] as String? ?? '').toUpperCase();
          currentGroup = divLabel.contains('TODAY') ? 'Today' : 'Pinned';
        } else if (sc['kind'] == 4) {
          final meta = ImmutableView._tryParseUiMeta(sc['detail']);
          if (meta?['_ui'] == 'navitem') {
            final label =
                meta!['label'] as String? ?? sc['text'] as String? ?? '';
            final iconSlug = meta['icon'] as String? ?? 'message-square';
            final selected = meta['selected'] == true;
            final badge = meta['badge'] as String?;
            final scChildren = (sc['children'] as List? ?? const [])
                .whereType<Map>()
                .toList();
            final actChild =
                _findChildByKeySuffix(scChildren, '.act') ??
                (scChildren.isNotEmpty ? scChildren.first : null);
            final navItem = _InoSessionNavItem(
              key: sk,
              label: label,
              icon: uiIcon(iconSlug) ?? LucideIcons.messageSquare,
              selected: selected,
              badge: badge,
              onTap: actChild != null
                  ? _dispatchButton(context, actChild)
                  : () => unawaited(_submitToInoComposer('/scenario $sk')),
            );
            if (currentGroup == 'Today') {
              todaySessions.add(navItem);
            } else {
              pinnedSessions.add(navItem);
            }
          }
        } else if (sc['kind'] == 9) {
          final entries = (sc['children'] as List? ?? const [])
              .whereType<Map>()
              .toList();
          if (entries.isNotEmpty) {
            final ek = entries.first['key'] as String? ?? '';
            final dot = ek.lastIndexOf('.');
            if (dot >= 0 && dot + 1 < ek.length) {
              activeConversationId = ek.substring(dot + 1);
            }
          }
        }
      }
    }

    onNewSession ??= enabled
        ? () => unawaited(_submitToInoComposer('/new'))
        : null;

    var sessionTitle = 'New chat';
    var statusText = 'Ready';
    var isListening = false;
    String? connectedDb;
    final parsedCards = <_InoParsedCard>[];
    final parsedTurns = <_InoParsedTurn>[];
    VoidCallback? onConfirmWaiting;
    VoidCallback? onDismissWaiting;

    if (mainNode != null) {
      final mainChildren = (mainNode['children'] as List? ?? const [])
          .whereType<Map>()
          .toList();
      String? pendingCardKind;
      String? pendingCardStatus;
      String? pendingCardTitle;
      String? pendingCardUri;

      for (var i = 0; i < mainChildren.length; i++) {
        final mc = mainChildren[i];
        if (i == 0 && mc['kind'] == 4) {
          sessionTitle = (mc['text'] as String?)?.isNotEmpty == true
              ? mc['text'] as String
              : 'New chat';
          final hdrChildren = (mc['children'] as List? ?? const [])
              .whereType<Map>()
              .toList();
          if (hdrChildren.isNotEmpty && hdrChildren[0]['kind'] == 3) {
            for (final pill
                in (hdrChildren[0]['children'] as List? ?? const [])
                    .whereType<Map>()) {
              final pt = pill['text'] as String? ?? '';
              final pi = pill['icon'] as String? ?? '';
              if (pt == 'Listening') isListening = true;
              if (pi == 'database') connectedDb = pt;
            }
          }
          if (hdrChildren.length > 1 && hdrChildren[1]['kind'] == 0) {
            statusText = hdrChildren[1]['text'] as String? ?? statusText;
          }
          continue;
        }

        if (mc['kind'] == 0) {
          final rawText = mc['text'] as String? ?? '';
          final match = RegExp(
            r'^\[([^:]+):([^\]]+)\]\s+(.+?)\s+\((neuron://[^|\s]+)',
          ).firstMatch(rawText);
          if (match != null) {
            pendingCardKind = match.group(1)!;
            pendingCardStatus = match.group(2)!;
            pendingCardTitle = match.group(3)!;
            pendingCardUri = match.group(4)!;
          }
          continue;
        }

        if (mc['kind'] == 4) {
          final meta = ImmutableView._tryParseUiMeta(mc['detail']);
          final uiKind = meta?['_ui'] as String? ?? '';
          final cardText = mc['text'] as String? ?? '';

          if (uiKind == 'surface' && cardText.startsWith('Waiting on you:')) {
            final surfaceChildren = (mc['children'] as List? ?? const [])
                .whereType<Map>()
                .toList();
            if (surfaceChildren.isNotEmpty && surfaceChildren[0]['kind'] == 3) {
              for (final btn
                  in (surfaceChildren[0]['children'] as List? ?? const [])
                      .whereType<Map>()) {
                if (btn['kind'] == 1) {
                  final bt = btn['text'] as String? ?? '';
                  if (bt.startsWith('Confirm')) {
                    onConfirmWaiting = _dispatchButton(context, btn);
                  } else if (bt.startsWith('Dismiss')) {
                    onDismissWaiting = _dispatchButton(context, btn);
                  }
                }
              }
            }
            continue;
          }

          if (uiKind == 'surface' &&
              (meta?['kind'] as String? ?? '') == 'elevated') {
            final turnChildren = (mc['children'] as List? ?? const [])
                .whereType<Map>()
                .toList();
            var routeHint = meta?['subtitle'] as String? ?? 'agent';
            String? recalledBadge;
            if (turnChildren.isNotEmpty && turnChildren[0]['kind'] == 3) {
              final pills = (turnChildren[0]['children'] as List? ?? const [])
                  .whereType<Map>()
                  .toList();
              if (pills.length > 1) {
                routeHint = pills[1]['text'] as String? ?? routeHint;
              }
              if (pills.length > 2) {
                recalledBadge = pills[2]['text'] as String?;
              }
            }
            final userMsg = turnChildren.length > 1
                ? (turnChildren[1]['text'] as String? ?? cardText)
                : cardText;
            final replyMsg = turnChildren.length > 2
                ? (turnChildren[2]['text'] as String? ?? '')
                : '';
            parsedTurns.add(
              _InoParsedTurn(
                userMessage: userMsg,
                reply: replyMsg,
                routeHint: routeHint,
                recalledBadge: recalledBadge,
              ),
            );
            continue;
          }

          if (const {
            'approval',
            'choice',
            'draft',
            'secret',
            'record',
            'datatable',
            'chart',
            'webbrowser',
            'markdraw',
            'neuronRef',
          }.contains(uiKind)) {
            final k = pendingCardKind ?? uiKind;
            final st =
                pendingCardStatus ??
                (meta?['state'] as String?) ??
                (meta?['sent'] == true || meta?['saved'] == true
                    ? 'approved'
                    : 'ready');
            final isWaiting =
                st.toLowerCase() == 'pending' &&
                const {
                  'approval',
                  'choice',
                  'draft',
                  'secret',
                  'app-install',
                }.contains(k.toLowerCase());
            parsedCards.add(
              _InoParsedCard(
                key: mc['key'] as String? ?? 'card-$i',
                kind: k,
                status: st,
                title: pendingCardTitle ?? cardText,
                sourceUri: pendingCardUri,
                isWaiting: isWaiting,
                widget: _node(context, mc, depth + 1),
              ),
            );
            pendingCardKind = null;
            pendingCardStatus = null;
            pendingCardTitle = null;
            pendingCardUri = null;
          }
        }
      }
    }

    return _InoChatDesktopWorkspace(
      activeConversationId: activeConversationId,
      sessionTitle: sessionTitle,
      statusText: statusText,
      isListening: isListening,
      connectedDb: connectedDb,
      pinnedSessions: pinnedSessions,
      todaySessions: todaySessions,
      cards: parsedCards,
      turns: parsedTurns,
      markdown: markdown,
      onNewSession: onNewSession,
      onConfirmWaiting: onConfirmWaiting,
      onDismissWaiting: onDismissWaiting,
      onSubmitPrompt: (prompt) => unawaited(_submitToInoComposer(prompt)),
      onOpenApp: (targetApp) {
        if (onActivate != null) {
          onActivate!({
            'kind': 'os_uri',
            'uri': 'os://window/open?app=$targetApp',
            'host': 'window',
            'target': 'open',
            'query': {'app': targetApp},
          });
        }
      },
    );
  }
}

