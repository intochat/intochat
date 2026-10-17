import 'package:flutter/material.dart';

import 'ui_neuron_button.dart';

/// A generic choice menu. The owner supplies its options and their order.
class UiNeuronSelect extends StatefulWidget {
  const UiNeuronSelect({
    super.key,
    required this.label,
    required this.options,
    this.selected,
    this.visual,
    this.onSelected,
  });
  final String label;
  final List<Map> options;
  final String? selected;
  final Map? visual;
  final ValueChanged<String>? onSelected;

  @override
  State<UiNeuronSelect> createState() => _UiNeuronSelectState();
}

class _UiNeuronSelectState extends State<UiNeuronSelect> {
  final _menu = MenuController();
  final _search = TextEditingController();
  final _searchFocus = FocusNode();
  String _query = '';

  @override
  void dispose() {
    _search.dispose();
    _searchFocus.dispose();
    super.dispose();
  }

  void _choose(Map option) {
    if (option['enabled'] == false) return;
    _menu.close();
    widget.onSelected?.call('${option['id']}');
  }

  @override
  Widget build(BuildContext context) {
    final matches = widget.options
        .where((option) => '${option['label']}'.toLowerCase().contains(_query))
        .toList();
    final searchable = widget.options.length > 8;
    return MenuAnchor(
      controller: _menu,
      onOpen: () {
        _search.clear();
        setState(() => _query = '');
        if (searchable) {
          WidgetsBinding.instance.addPostFrameCallback((_) {
            if (mounted && _menu.isOpen) _searchFocus.requestFocus();
          });
        }
      },
      menuChildren: [
        SizedBox(
          width: 280,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (searchable)
                Padding(
                  padding: const EdgeInsets.all(8),
                  child: TextField(
                    controller: _search,
                    focusNode: _searchFocus,
                    decoration: const InputDecoration(
                      labelText: 'Search options',
                      prefixIcon: Icon(Icons.search),
                      isDense: true,
                    ),
                    onChanged: (value) =>
                        setState(() => _query = value.trim().toLowerCase()),
                    onSubmitted: (_) {
                      final first = matches
                          .where((item) => item['enabled'] != false)
                          .firstOrNull;
                      if (first != null) _choose(first);
                    },
                  ),
                ),
              ConstrainedBox(
                constraints: BoxConstraints(
                  maxHeight: MediaQuery.sizeOf(context).height * .55,
                ),
                child: SingleChildScrollView(
                  primary: false,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      if (matches.isEmpty)
                        Padding(
                          padding: const EdgeInsets.all(16),
                          child: Semantics(
                            liveRegion: true,
                            child: const Text('No matching options'),
                          ),
                        ),
                      for (final option in matches)
                        MenuItemButton(
                          onPressed: option['enabled'] == false
                              ? null
                              : () => _choose(option),
                          leadingIcon: widget.selected == null
                              ? null
                              : SizedBox(
                                  width: 20,
                                  child: option['id'] == widget.selected
                                      ? const Icon(Icons.check, size: 18)
                                      : null,
                                ),
                          child: option['visual'] is Map
                              ? SizedBox(
                                  width: widget.selected == null ? 248 : 216,
                                  child: UiNeuronButton(
                                    label: '${option['label']}',
                                    visual: {
                                      ...option['visual'] as Map,
                                      'compact': false,
                                    },
                                    menuChild: true,
                                  ),
                                )
                              : Text('${option['label']}'),
                        ),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
      ],
      builder: (context, controller, child) => Tooltip(
        message: widget.label,
        child: TextButton(
          onPressed: widget.onSelected == null
              ? null
              : () =>
                    controller.isOpen ? controller.close() : controller.open(),
          style: TextButton.styleFrom(padding: EdgeInsets.zero),
          child: widget.visual != null
              ? UiNeuronButton(
                  label: widget.label,
                  visual: widget.visual!,
                  menuChild: true,
                )
              : Text(
                  '${widget.options.where((option) => option['id'] == widget.selected).firstOrNull?['label'] ?? widget.label}',
                ),
        ),
      ),
    );
  }
}
