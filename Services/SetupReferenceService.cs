using AvoTelemetryAgent.Models;

namespace AvoTelemetryAgent.Services;

/// <summary>
/// Scans Setup.ReferenceRoot for *.ini files organised as
///   {ReferenceRoot}/{car}/{track}/*.ini
/// and caches the result for 2 seconds (or until Rescan() is called or the
/// root path changes).
/// </summary>
public sealed class SetupReferenceService
{
    private const int CacheTtlSeconds = 2;

    private readonly AgentConfigService              _cfgSvc;
    private readonly ILogger<SetupReferenceService>  _log;
    private readonly object                          _lock = new();
    private CachedTree?                              _cache;

    // ── Observable counters (updated on each scan) ────────────────────────────
    public int TotalCount { get; private set; }
    public int CarsCount  { get; private set; }

    public SetupReferenceService(
        AgentConfigService             cfgSvc,
        ILogger<SetupReferenceService> log)
    {
        _cfgSvc = cfgSvc;
        _log    = log;
    }

    /// <summary>Forces the next read to re-scan the filesystem.</summary>
    public void Rescan()
    {
        lock (_lock) { _cache = null; }
        GetOrBuild(); // eager re-scan so TotalCount is immediately correct
    }

    // ── Query methods ─────────────────────────────────────────────────────────

    /// <summary>Sorted list of car folder names found under ReferenceRoot.</summary>
    public string[] GetCars()
        => GetOrBuild().Tree.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Sorted list of track folder names inside a car folder.</summary>
    public string[] GetTracks(string car)
    {
        var tree = GetOrBuild().Tree;
        return tree.TryGetValue(car, out var tracks)
            ? tracks.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    /// <summary>
    /// Setup files inside a specific car/track folder,
    /// sorted by UpdatedUtc desc then FileName asc.
    /// </summary>
    public IReadOnlyList<SetupRefItem> GetSetups(string car, string track)
    {
        var tree = GetOrBuild().Tree;
        if (!tree.TryGetValue(car,   out var tracks)) return [];
        if (!tracks.TryGetValue(track, out var items)) return [];
        return [.. items
            .OrderByDescending(i => i.UpdatedUtc)
            .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Flat list of all items, optionally filtered by carId and/or trackId.
    /// </summary>
    public IReadOnlyList<SetupRefItem> GetAllItems(
        string? carId   = null,
        string? trackId = null)
    {
        var tree = GetOrBuild().Tree;
        IEnumerable<SetupRefItem> all =
            tree.Values.SelectMany(t => t.Values.SelectMany(v => v));

        if (carId   is not null)
            all = all.Where(i => i.CarId.Equals(carId,   StringComparison.OrdinalIgnoreCase));
        if (trackId is not null)
            all = all.Where(i => i.TrackId.Equals(trackId, StringComparison.OrdinalIgnoreCase));

        return [.. all
            .OrderBy(i => i.CarId,   StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.TrackId,  StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private CachedTree GetOrBuild()
    {
        var root = _cfgSvc.Current.Setup.ReferenceRoot;
        lock (_lock)
        {
            if (_cache is not null &&
                _cache.Root == root &&
                DateTime.UtcNow < _cache.ExpiresAt)
                return _cache;

            var tree  = ScanRoot(root);
            TotalCount = tree.Values.SelectMany(t => t.Values).Sum(l => l.Count);
            CarsCount  = tree.Count;
            _cache     = new CachedTree(tree, root,
                DateTime.UtcNow.AddSeconds(CacheTtlSeconds));
            return _cache;
        }
    }

    private Dictionary<string, Dictionary<string, List<SetupRefItem>>> ScanRoot(string root)
    {
        var tree = new Dictionary<string, Dictionary<string, List<SetupRefItem>>>(
            StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return tree;

        try
        {
            foreach (var carDir in Directory.EnumerateDirectories(root))
            {
                var carId = Path.GetFileName(carDir);
                if (string.IsNullOrEmpty(carId)) continue;

                var trackTree = new Dictionary<string, List<SetupRefItem>>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var trackDir in Directory.EnumerateDirectories(carDir))
                {
                    var trackId = Path.GetFileName(trackDir);
                    if (string.IsNullOrEmpty(trackId)) continue;

                    var setups = new List<SetupRefItem>();
                    foreach (var iniFile in Directory.EnumerateFiles(trackDir, "*.ini"))
                    {
                        var fi = new FileInfo(iniFile);
                        setups.Add(new SetupRefItem
                        {
                            CarId        = carId,
                            TrackId      = trackId,
                            FileName     = fi.Name,
                            DisplayName  = Path.GetFileNameWithoutExtension(fi.Name),
                            RelativePath = Path.Combine(carId, trackId, fi.Name),
                            UpdatedUtc   = fi.LastWriteTimeUtc,
                            SizeBytes    = fi.Length,
                        });
                    }
                    if (setups.Count > 0) trackTree[trackId] = setups;
                }

                if (trackTree.Count > 0) tree[carId] = trackTree;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error scanning reference root: {Root}", root);
        }

        return tree;
    }

    private sealed record CachedTree(
        Dictionary<string, Dictionary<string, List<SetupRefItem>>> Tree,
        string   Root,
        DateTime ExpiresAt);
}
