import 'dart:async';

import 'package:digitalbrain_flutter/digitalbrain_flutter.dart';
import 'package:flutter/material.dart';

import '../home/app_icon.dart';
import 'assistant_island.dart';

typedef DockRequest = Future<void> Function(String operation, String input);

// The focused app's toolbar. Every entry posts the app a request and waits for nothing: the
// app's answer is neuron state its surface shows.
class Dock extends StatelessWidget {
  const Dock({
    super.key,
    required this.app,
    required this.manifest,
    required this.onRequest,
    this.loadIcon,
    this.iconRoute,
    this.watchActivity,
    this.floating = false,
  });

  final String app;
  final AppManifest manifest;
  final DockRequest onRequest;
  final IconLoader? loadIcon;
  // The route serving the app's own icon file; an entry naming that file renders it.
  final String? iconRoute;
  // The brain's AppActivity feed Ino follows; floating is true while no app is focused.
  final Stream<AppActivity> Function()? watchActivity;
  final bool floating;

  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      if (app == 'assistant')
        AssistantIsland(watch: watchActivity, floating: floating),
      for (final entry in manifest.toolbar)
        _DockEntry(
          key: ValueKey('dock-entry-${entry.operation}'),
          entry: entry,
          manifestIcon: manifest.icon,
          iconRoute: iconRoute,
          loadIcon: loadIcon,
          onRequest: onRequest,
        ),
    ],
  );
}

class _DockEntry extends StatefulWidget {
  const _DockEntry({
    super.key,
    required this.entry,
    required this.onRequest,
    this.manifestIcon,
    this.iconRoute,
    this.loadIcon,
  });

  final AppToolbarEntry entry;
  final DockRequest onRequest;
  final String? manifestIcon, iconRoute;
  final IconLoader? loadIcon;

  @override
  State<_DockEntry> createState() => _DockEntryState();
}

class _DockEntryState extends State<_DockEntry> {
  bool _on = false;
  double? _value;

  AppToolbarEntry get entry => widget.entry;
  String get label => entry.label ?? entry.operation;

  void _post(String input) =>
      unawaited(widget.onRequest(entry.operation, input));

  Widget _icon() {
    final name = entry.icon;
    final data = name == null ? null : shellIcon(name);
    if (data != null) return Icon(data, size: 22);
    if (name != null &&
        name == widget.manifestIcon &&
        widget.iconRoute != null) {
      return AppIcon(
        title: label,
        path: widget.iconRoute,
        load: widget.loadIcon,
        size: 22,
      );
    }
    return Text(label);
  }

  static String _text(double value) => value == value.roundToDouble()
      ? value.toInt().toString()
      : value.toString();

  @override
  Widget build(BuildContext context) {
    final min = entry.min ?? 0, max = entry.max ?? 1;
    return switch (entry.kind) {
      AppToolbarKind.button => IconButton(
        key: ValueKey('dock-${entry.operation}'),
        tooltip: label,
        onPressed: () => _post(''),
        icon: _icon(),
      ),
      AppToolbarKind.toggle => Tooltip(
        message: label,
        child: Switch(
          key: ValueKey('dock-${entry.operation}'),
          value: _on,
          onChanged: (value) {
            setState(() => _on = value);
            _post(value ? 'on' : 'off');
          },
        ),
      ),
      AppToolbarKind.slider => SizedBox(
        width: 140,
        child: Slider(
          key: ValueKey('dock-${entry.operation}'),
          min: min,
          max: max,
          value: (_value ?? min).clamp(min, max),
          label: label,
          onChanged: (value) => setState(() => _value = value),
          onChangeEnd: (value) => _post(_text(value)),
        ),
      ),
      AppToolbarKind.text => DockTextField(
        key: ValueKey('dock-${entry.operation}'),
        hint: label,
        onSubmit: _post,
      ),
    };
  }
}
