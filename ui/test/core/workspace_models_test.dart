import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  test('typed workspace preserves view identity context and pins', () {
    final state = WorkspaceSnapshot.fromJson({
      'revision': 3,
      'path': ['brain'],
      'open': [],
      'openViews': [
        {
          'id': 'person',
          'app': 'brain/apps/profile',
          'variant': 'full',
          'context': 'person:alice',
        },
      ],
      'focusedViewId': 'person',
      'pins': [
        {
          'id': 'calc',
          'app': 'brain/apps/calculator',
          'variant': 'compact',
          'context': '',
        },
      ],
    });
    expect(state.openViews.single.context, 'person:alice');
    expect(state.focusedViewId, 'person');
    expect(state.pins.single.variant, 'compact');
    expect(() => state.openViews.clear(), throwsUnsupportedError);
  });
  test('workspace snapshot retains navigation path and open apps', () {
    final state = WorkspaceSnapshot.fromJson({
      'revision': 4,
      'path': ['brain', 'brain/apps/ino', 'brain/apps/files'],
      'open': ['brain/apps/ino', 'brain/apps/files'],
    });
    expect(state.revision, 4);
    expect(state.path.last, 'brain/apps/files');
    expect(state.open, ['brain/apps/ino', 'brain/apps/files']);
    expect(() => state.path.clear(), throwsUnsupportedError);
    expect(() => state.open.clear(), throwsUnsupportedError);
  });
}
