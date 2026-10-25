# APP_NAME

A DigitalBrain app. `APP_NAME/APP_CLASSApp.cs` declares the app and `APP_NAME/GreeterNeuron.cs` its one neuron.

```
dotnet build
dotnet test --project APP_NAME.Tests.Unit/APP_NAME.Tests.Unit.csproj
```

The tests use the `DigitalBrain.Testing` fakes (`NeuronHarness`), so they run without a runtime.
TestOS arrives in a later DigitalBrain release.
