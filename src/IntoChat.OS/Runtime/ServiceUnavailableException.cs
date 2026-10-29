namespace IntoChat.OS;

// A refusal word: the system behind a neuron is down or not provisioned. HTTP surfaces map it to 503.
[GenerateSerializer, Alias("framework.service-unavailable")]
public class ServiceUnavailableException(string message) : Exception(message);
