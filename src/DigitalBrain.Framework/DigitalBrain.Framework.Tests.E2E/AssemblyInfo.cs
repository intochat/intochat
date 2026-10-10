// The reference host shares its model endpoint and shell state; facts run serially.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
[assembly: Xunit.AssemblyFixture(typeof(DigitalBrain.OS.Testing.ReferenceBrainFixture))]
[assembly: Xunit.AssemblyFixture(typeof(DigitalBrain.OS.Testing.ProductionBrainFixture))]
[assembly: Xunit.AssemblyFixture(typeof(DigitalBrain.OS.Testing.ProductionCaptureOffFixture))]
