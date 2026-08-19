using EgressController.Core.Contracts;
using EgressController.Rules.Matcher;
using EgressController.Rules.Parsing;

namespace EgressController.Rules.Stores;

/// <summary>
/// Holds the user's selected geosite rule sets + manual domains and exposes an atomically-swapped,
/// immutable <see cref="IDomainMatcher"/> snapshot. Manual rules always come first (priority §1.4);
/// selected sets follow in user order. Any mutation rebuilds a fresh matcher and publishes it via a
/// <c>volatile</c> write, so concurrent routing reads always see a consistent snapshot.
/// </summary>
public sealed class DomainSetStore
{
    private readonly object _gate = new();
    private FreezableState _state = new();

    public IDomainMatcher Current => GetMatcher();

    /// <summary>Names of the currently active local/manual rules, for the control surface.</summary>
    public IReadOnlyList<string> ManualDomains
    {
        get
        {
            lock (_gate)
                return _state.Manual.Select(r => r.Base).ToArray();
        }
    }

    /// <summary>Names of selected geosite sets in their routing priority order.</summary>
    public IReadOnlyList<string> SelectedSetNames
    {
        get
        {
            lock (_gate)
                return _state.Sets.Select(s => s.Name).ToArray();
        }
    }

    private sealed class FreezableState
    {
        public List<CompiledDomainRule> Manual { get; } = new();
        public List<NamedSet> Sets { get; } = new();
        public DomainMatcher? Matcher;
    }

    private sealed record NamedSet(string Name, IReadOnlyList<CompiledDomainRule> Rules);

    /// <summary>Add a manual domain (default ∪ subdomains = SuffixInclusive, §1.4).</summary>
    public void AddManual(string host)
    {
        lock (_gate)
        {
            var rule = StrictDomainListParser.ManualDefault(host, "manual");
            if (_state.Manual.Any(r => r.Base == rule.Base && r.Kind == rule.Kind))
                return;
            _state.Manual.Add(rule);
            RefreshMatcherLocked();
        }
    }

    public void RemoveManual(string host)
    {
        var rule = StrictDomainListParser.ManualDefault(host, "manual");
        lock (_gate)
        {
            _state.Manual.RemoveAll(r => r.Base == rule.Base);
            RefreshMatcherLocked();
        }
    }

    /// <summary>Atomically replace the entire selected rule-set lineup.</summary>
    public void ReplaceSelectedSets(IReadOnlyDictionary<string, IReadOnlyList<CompiledDomainRule>> sets)
    {
        lock (_gate)
        {
            _state.Sets.Clear();
            foreach (var kv in sets)
                _state.Sets.Add(new NamedSet(kv.Key, kv.Value));
            RefreshMatcherLocked();
        }
    }

    public IDomainMatcher GetMatcher()
    {
        // Fast path: already built.
        DomainMatcher? m = _state.Matcher;
        if (m is not null)
            return m;
        lock (_gate)
        {
            RefreshMatcherLocked();
            return _state.Matcher!;
        }
    }

    private void RefreshMatcherLocked()
    {
        var views = new List<DomainMatcher.RuleSetView>(_state.Manual.Count + _state.Sets.Count);
        if (_state.Manual.Count > 0)
            views.Add(new DomainMatcher.RuleSetView("manual", _state.Manual));
        foreach (var set in _state.Sets)
            views.Add(new DomainMatcher.RuleSetView(set.Name, set.Rules));
        _state.Matcher = new DomainMatcher(views);
    }
}
