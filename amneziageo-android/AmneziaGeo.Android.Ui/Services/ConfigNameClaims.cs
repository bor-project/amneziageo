namespace AmneziaGeo.Android.Ui.Services;

internal sealed class ConfigNameClaims
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _configs;
    private readonly Dictionary<string, Lease> _claims = new(StringComparer.Ordinal);

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigNameClaims(Dictionary<string, string> configs)
    {
        _configs = configs;
    }

    public string[] Names()
    {
        lock (_gate)
        {
            return [.. _configs.Keys.Concat(_claims.Keys).Distinct(StringComparer.Ordinal)];
        }
    }

    public Dictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_configs, StringComparer.Ordinal);
        }
    }

    public T Snapshot<T>(Func<Dictionary<string, string>, T> capture)
    {
        lock (_gate)
        {
            return capture(new Dictionary<string, string>(_configs, StringComparer.Ordinal));
        }
    }

    public Lease? TryAcquire(IReadOnlyCollection<string> names, out string? busy)
    {
        lock (_gate)
        {
            return TryAcquireLocked(names, out busy);
        }
    }

    public Lease? TryAcquirePlanned(
        Func<Dictionary<string, string>, HashSet<string>, IReadOnlyCollection<string>> plan,
        out string? busy)
    {
        lock (_gate)
        {
            var current = new Dictionary<string, string>(_configs, StringComparer.Ordinal);
            var taken = new HashSet<string>(_configs.Keys.Concat(_claims.Keys), StringComparer.Ordinal);
            return TryAcquireLocked(plan(current, taken), out busy);
        }
    }

    private Lease? TryAcquireLocked(IReadOnlyCollection<string> names, out string? busy)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToArray();
        busy = distinct.FirstOrDefault(_claims.ContainsKey);
        if (busy is not null)
        {
            return null;
        }

        var lease = new Lease(this, distinct);
        foreach (var name in distinct)
        {
            _claims.Add(name, lease);
        }

        return lease;
    }

    private bool Owns(Lease lease, string name) => !lease.Released
        && _claims.TryGetValue(name, out var held) && ReferenceEquals(lease, held);

    internal sealed class Lease : IDisposable
    {
        private readonly ConfigNameClaims _owner;
        private readonly string[] _names;
        internal bool Released { get; private set; }

        /// <summary>
        /// ctor
        /// </summary>
        internal Lease(ConfigNameClaims owner, string[] names)
        {
            _owner = owner;
            _names = names;
        }

        public string? Read(string name)
        {
            lock (_owner._gate)
            {
                Require(name);
                return _owner._configs.GetValueOrDefault(name);
            }
        }

        public bool Write(string name, string text, bool createOnly = false)
        {
            lock (_owner._gate)
            {
                if (!_owner.Owns(this, name) || (createOnly && _owner._configs.ContainsKey(name)))
                {
                    return false;
                }

                _owner._configs[name] = text;
                return true;
            }
        }

        public bool Drop(string name, Action changed)
        {
            lock (_owner._gate)
            {
                if (!_owner.Owns(this, name))
                {
                    return false;
                }

                _owner._configs.Remove(name);
                changed();
                return true;
            }
        }

        public bool Move(string source, string destination, Action changed)
        {
            lock (_owner._gate)
            {
                if (!_owner.Owns(this, source) || !_owner.Owns(this, destination)
                    || !_owner._configs.TryGetValue(source, out var text)
                    || _owner._configs.ContainsKey(destination))
                {
                    return false;
                }

                _owner._configs.Remove(source);
                _owner._configs[destination] = text;
                changed();
                return true;
            }
        }

        private void Require(string name)
        {
            if (!_owner.Owns(this, name))
            {
                throw new InvalidOperationException("The configuration name claim is no longer held.");
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (Released)
                {
                    return;
                }

                foreach (var name in _names)
                {
                    if (_owner._claims.TryGetValue(name, out var held) && ReferenceEquals(this, held))
                    {
                        _owner._claims.Remove(name);
                    }
                }

                Released = true;
            }
        }
    }
}
