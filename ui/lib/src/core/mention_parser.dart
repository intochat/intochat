import 'package:flutter/widgets.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

/// Parsed active `@mention` token at the current composer caret.
typedef ActiveMentionToken = ({int start, int end, String query});

final RegExp _mentionBoundaryRe = RegExp(r'[\s(\["\x27,:;]');
final RegExp _whitespaceRe = RegExp(r'\s');
final RegExp _leadingAtRe = RegExp(r'^@+');
final RegExp _handleSplitRe = RegExp(r'[\s·]+');

/// Extracts the active `@handle` token at the caret, respecting a dismissed offset.
ActiveMentionToken? parseActiveMentionToken(
  String text,
  TextSelection selection, {
  int? dismissedOffset,
}) {
  if (!selection.isValid || !selection.isCollapsed) return null;
  final cursor = selection.baseOffset;
  if (cursor <= 0 || cursor > text.length) return null;
  var at = cursor - 1;
  while (at >= 0) {
    final ch = text[at];
    if (ch == '@') {
      if (at > 0 && !_mentionBoundaryRe.hasMatch(text[at - 1])) {
        return null;
      }
      if (dismissedOffset == at) return null;
      final query = text.substring(at + 1, cursor);
      if (query.contains(_whitespaceRe)) return null;
      return (start: at, end: cursor, query: query);
    }
    if (_whitespaceRe.hasMatch(ch)) return null;
    at--;
  }
  return null;
}

/// Builds deduplicated `@mention` candidates from `@Ino` and thread message senders.
List<String> resolveMentionCandidates(
  List<MessageNode> messages,
  String query,
) {
  final seen = <String>{};
  final all = <String>[];
  void addHandle(String raw) {
    final clean = raw.trim().replaceFirst(_leadingAtRe, '');
    if (clean.isEmpty) return;
    final token = clean.split(_handleSplitRe).first;
    if (token.isEmpty) return;
    if (seen.add(token.toLowerCase())) {
      all.add(token);
    }
  }

  addHandle('Ino');
  for (final m in messages) {
    final sender = m.sender;
    if (sender != null) {
      if (sender.accountId.isNotEmpty) addHandle(sender.accountId);
      if (sender.displayName.isNotEmpty) addHandle(sender.displayName);
    }
  }
  final q = query.trim().toLowerCase();
  if (q.isEmpty) return all.take(6).toList(growable: false);
  return all
      .where((h) => h.toLowerCase().contains(q))
      .take(6)
      .toList(growable: false);
}
