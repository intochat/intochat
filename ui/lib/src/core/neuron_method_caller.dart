/// Invokes a typed method on a neuron contract over `NeuronChannel` (gRPC).
///
/// A read channel only: a chat thread reads its conversation through it (owner decision 10). Actions are commands on
/// `POST /brains/{id}/act`.
typedef NeuronMethodCaller = Future<Map<String, dynamic>?> Function(
  String contract,
  String key,
  String method,
  List<Object?> arguments,
);
