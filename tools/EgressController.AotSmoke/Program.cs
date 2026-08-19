// Step 00: minimal Avalonia-less NativeAOT smoke target.
// Purpose: prove that the SDK/toolchain can actually PublishAot and the binary runs,
// with TreatWarningsAsErrors + IsAotCompatible active (no IL/AOT warnings allowed).
Console.WriteLine("EgressController.AotSmoke: NativeAOT smoke OK.");
Console.WriteLine($"Runtime version: {Environment.Version}");
Console.WriteLine($"Is 64-bit OS: {Environment.Is64BitOperatingSystem}");
return 0;