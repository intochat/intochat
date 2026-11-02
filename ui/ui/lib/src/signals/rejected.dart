// Mirror of DigitalBrain.Rules.Rejected: a request the rules refused, named by
// the operation, the refusing rules and their messages.
final class Rejected {
  static const alias = 'rule.rejected';

  const Rejected({
    required this.operation,
    required this.rules,
    required this.messages,
  });

  factory Rejected.fromJson(Map<String, dynamic> json) => Rejected(
    operation: json['operation'] as String,
    rules: List<String>.unmodifiable(json['rules'] as List),
    messages: List<String>.unmodifiable(json['messages'] as List),
  );

  final String operation;
  final List<String> rules;
  final List<String> messages;

  Map<String, dynamic> toJson() => {
    'operation': operation,
    'rules': rules,
    'messages': messages,
  };
}
