import 'package:flutter/material.dart';

// The cat: the assistant's face in the middle of the dock. The rest of the island is the
// assistant app's own toolbar, rendered by the dock like any other.
class AssistantCat extends StatelessWidget {
  const AssistantCat({super.key});

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 6),
      child: CircleAvatar(
        key: const ValueKey('assistant-cat'),
        radius: 16,
        backgroundColor: scheme.primaryContainer,
        child: Icon(
          Icons.pets_rounded,
          size: 18,
          color: scheme.onPrimaryContainer,
        ),
      ),
    );
  }
}

// The text kind: one line; submit posts the trimmed text and clears the field.
class DockTextField extends StatefulWidget {
  const DockTextField({super.key, required this.hint, required this.onSubmit});

  final String hint;
  final ValueChanged<String> onSubmit;

  @override
  State<DockTextField> createState() => _DockTextFieldState();
}

class _DockTextFieldState extends State<DockTextField> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _submit(String text) {
    final trimmed = text.trim();
    if (trimmed.isEmpty) return;
    widget.onSubmit(trimmed);
    _controller.clear();
  }

  @override
  Widget build(BuildContext context) => SizedBox(
    width: 360,
    child: TextField(
      controller: _controller,
      decoration: InputDecoration(
        hintText: widget.hint,
        isDense: true,
        border: const OutlineInputBorder(),
      ),
      textInputAction: TextInputAction.send,
      onSubmitted: _submit,
    ),
  );
}
