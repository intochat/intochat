import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

void main() {
  test('decodes a rule.rejected payload', () {
    final json =
        jsonDecode(
              '{ "operation": "add", "rules": ["positive"], '
              '"messages": ["-1 is not positive."] }',
            )
            as Map<String, dynamic>;

    final rejected = Rejected.fromJson(json);

    expect(Rejected.alias, 'rule.rejected');
    expect(rejected.operation, 'add');
    expect(rejected.rules, ['positive']);
    expect(rejected.messages, ['-1 is not positive.']);
    expect(rejected.toJson(), json);
  });
}
