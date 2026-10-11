import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:test/test.dart';

void main() {
  test('a scene state round-trips the wire shape the C# neuron serializes', () {
    final json = {
      'name': 'owner/apps/ino/scenes/home',
      'revision': 3,
      'definition': {
        'nodes': [
          {
            'id': 'sphere',
            'type': 'ino_sphere',
            'transform': {
              'x': 0,
              'y': 0,
              'z': 0,
              'scale': 1,
              'rotation': 0,
              'opacity': 1,
            },
            'style': null,
            'parameters': {'mood': 'calm'},
            'children': [],
            'tweens': [],
            'inputs': ['tap'],
          },
        ],
        'machines': [
          {
            'id': 'presence',
            'initial': 'idle',
            'states': [
              {'name': 'idle', 'apply': []},
              {
                'name': 'thinking',
                'apply': [
                  {
                    'nodeId': 'sphere',
                    'style': null,
                    'parameters': {'mood': 'busy'},
                    'transform': null,
                    'tweens': null,
                  },
                ],
              },
            ],
            'transitions': [
              {'from': 'idle', 'event': 'think', 'to': 'thinking'},
            ],
          },
        ],
        'camera': {'x': 0, 'y': 0, 'zoom': 1},
        'background': '#0B0F14',
      },
      'current': {'presence': 'thinking'},
    };
    final state = SceneState.fromJson(json);
    expect(state.revision, 3);
    expect(state.definition.nodes.single.type, 'ino_sphere');
    expect(state.current['presence'], 'thinking');
    expect(
      state
          .definition
          .machines
          .single
          .states[1]
          .apply
          .single
          .parameters?['mood'],
      'busy',
    );
  });
}
