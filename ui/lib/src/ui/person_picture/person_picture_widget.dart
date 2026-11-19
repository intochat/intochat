import 'dart:async';
import 'package:flutter/material.dart';

import '../../design/tokens.dart';
import '../neuron_widget.dart';

class PersonPictureNeuronWidget extends StatelessWidget {
  const PersonPictureNeuronWidget({
    super.key,
    required this.neuronKey,
    required this.callNeuron,
    this.watch,
  });

  final String neuronKey;
  final NeuronMethodCaller callNeuron;
  final Stream<void> Function(String kind, String name)? watch;

  @override
  Widget build(BuildContext context) {
    return NeuronWidget(
      kind: 'ui.person-picture',
      neuronKey: neuronKey,
      callNeuron: callNeuron,
      watch: watch,
      builder: (context, state, invoke) {
        final c = IcTheme.of(context);
        final name = (state['displayName'] as String?) ?? 'Owner';
        final initials = (state['initials'] as String?) ?? 'OW';
        final subtitle = state['subtitle'] as String?;
        final presence = (state['presence'] as String?) ?? 'online';
        final presenceColor = switch (presence) {
          'online' => c.done,
          'busy' => c.error,
          'away' => c.working,
          _ => c.textMuted,
        };

        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(IcRadius.sm),
            border: Border.all(color: c.borderHairline),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Stack(
                children: [
                  CircleAvatar(
                    radius: 18,
                    backgroundColor: c.accent.withValues(alpha: 0.16),
                    child: Text(initials, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: c.accent)),
                  ),
                  Positioned(
                    right: 0,
                    bottom: 0,
                    child: Container(
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: presenceColor,
                        shape: BoxShape.circle,
                        border: Border.all(color: c.surfaceCard, width: 1.5),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(width: 10),
              Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(name, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: c.textStrong)),
                  Text(subtitle ?? presence, style: TextStyle(fontSize: 11, color: c.textMuted)),
                ],
              ),
            ],
          ),
        );
      },
    );
  }
}
