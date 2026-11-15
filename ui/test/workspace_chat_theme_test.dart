import 'package:intochat_os_flutter_shell/src/theme/ui_theme.dart';
import 'package:intochat_os_flutter_shell/src/chat/ui_copyable_message.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('workspace primary actions use the workspace font', () {
    final style = UiTheme.workspace(Brightness.light)
        .filledButtonTheme
        .style!
        .textStyle!
        .resolve({})!;
    expect(style.fontFamily, UiType.bodyFamily);
    expect(style.fontFamilyFallback, UiType.bodyFallback);
  });
  testWidgets('copy action reserves space beside message content', (
    tester,
  ) async {
    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: SizedBox(
            width: 340,
            child: UiCopyableMessage(
              copyText: (_) => 'Message',
              child: const SizedBox(
                key: Key('message_body'),
                height: 50,
                width: double.infinity,
              ),
            ),
          ),
        ),
      ),
    );
    final body = tester.getRect(find.byKey(const Key('message_body')));
    final copy = tester.getRect(find.byKey(const Key('ui_copy_message')));
    expect(copy.left, greaterThanOrEqualTo(body.right));
  });
}
