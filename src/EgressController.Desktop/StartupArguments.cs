namespace EgressController.Desktop;

public enum StartupMode { Production, Preview, MockSmoke }
public sealed record StartupSelection(StartupMode Mode, string Scenario = "overview");

/// <summary>Pure argument dispatch. Resolving a mode never constructs native or production services.</summary>
public static class StartupArguments
{
    private static readonly HashSet<string> Scenarios = new(["overview", "apps", "add-apps", "rules", "add-rules", "nodes", "settings", "dsl", "onboarding", "empty", "error", "stopped", "unavailable"], StringComparer.Ordinal);
    public static void EnsureIsolationCompatible(StartupMode mode, bool mockOnly)
    {
        if (mode == StartupMode.Production && mockOnly) throw new InvalidOperationException("Production startup is blocked while mock safety is active; use explicit --preview or start in a clean environment.");
    }
    public static StartupSelection Resolve(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        StartupMode mode = StartupMode.Production; bool selected = false; string scenario = "overview"; bool scenarioSelected = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--preview":
                case "--mock-smoke":
                    if (selected) throw new ArgumentException("Choose one startup mode.");
                    selected = true; mode = arguments[index] == "--preview" ? StartupMode.Preview : StartupMode.MockSmoke; break;
                case "--scenario":
                    if (scenarioSelected || index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Specify exactly one preview scenario.");
                    scenarioSelected = true; scenario = arguments[++index]; if (!Scenarios.Contains(scenario)) throw new ArgumentException("Unknown preview scenario."); break;
                default: throw new ArgumentException("Unknown startup argument.");
            }
        }
        if (scenarioSelected && mode != StartupMode.Preview) throw new ArgumentException("Scenarios are only available with explicit --preview.");
        return new(mode, scenario);
    }
}
