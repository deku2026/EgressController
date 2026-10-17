using EgressController.Core.Protection;
using EgressController.Guardian;

if (args is ["--mock-smoke"]) return GuardSmoke.Run();
RuntimeSafety.RequireLiveOperations();
if (args is not ["--owner", var sid]) throw new ArgumentException("This executable runs only as an installed Windows service.");
return GuardServiceHost.Run(GuardPaths.ForSid(sid));
