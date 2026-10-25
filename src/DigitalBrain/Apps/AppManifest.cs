using DigitalBrain.Capabilities;

namespace DigitalBrain;

/// <summary>What an app is and what it requires.</summary>
/// <param name="Id">The frozen app id.</param>
/// <param name="Name">The display name.</param>
/// <param name="Version">The app version.</param>
/// <param name="Neurons">The neuron classes the app brings.</param>
/// <param name="Requires">What the app requires from the brain.</param>
public sealed record AppManifest(string Id, string Name, string Version, IReadOnlyList<Type> Neurons, AppRequirements Requires);

/// <summary>What an app requires from the brain.</summary>
/// <param name="Capabilities">The capabilities the app calls.</param>
public sealed record AppRequirements(IReadOnlyList<CapabilityId> Capabilities);
