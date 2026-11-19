using System.Numerics;

namespace VIP_PlayerTrail;

internal enum MineMode { All, Team, Self }
internal enum OthersMode { All, Team, None }

internal static class TrailSelection
{
    internal const string Off = "off";

    internal static string Resolve(string? saved, List<(string Name, string Value)> options)
    {
        if (saved == Off) return Off;
        if (saved != null)
            foreach (var option in options)
                if (option.Value.Equals(saved, StringComparison.OrdinalIgnoreCase))
                    return saved;

        return options.Count > 0 ? options[0].Value : Off;
    }
}

internal static class TrailMovement
{
    // Keep the previous anchor until the movement is large enough to consume it.
    internal static bool Advance(ref Vector3 anchor, Vector3 position, out Vector3 from)
    {
        from = anchor;
        var distanceSquared = Vector3.DistanceSquared(anchor, position);
        if (distanceSquared < 12f * 12f) return false;
        anchor = position;
        return distanceSquared < 250f * 250f;
    }
}

internal sealed class TrailVisibility
{
    internal const int Slots = 65;

    private readonly int[] _teams = new int[Slots];
    private readonly MineMode[] _mine = new MineMode[Slots];
    private readonly OthersMode[] _others = new OthersMode[Slots];
    private readonly UInt128[] _hidden = new UInt128[Slots];
    private int _restricted;

    internal bool IsRestricted => _restricted != 0;
    internal MineMode Mine(int slot) => _mine[slot];
    internal OthersMode Others(int slot) => _others[slot];
    internal UInt128 HiddenOwners(int viewer) => _hidden[viewer];
    internal static UInt128 Bit(int slot) => (UInt128)1 << slot;

    internal void SetTeam(int slot, int team)
    {
        if (_teams[slot] == team) return;
        _teams[slot] = team;
        Refresh(slot);
    }

    internal void SetPreferences(int slot, MineMode mine, OthersMode others)
    {
        if (_mine[slot] == mine && _others[slot] == others) return;
        if (_mine[slot] != MineMode.All || _others[slot] != OthersMode.All) _restricted--;
        _mine[slot] = mine;
        _others[slot] = others;
        if (mine != MineMode.All || others != OthersMode.All) _restricted++;
        Refresh(slot);
    }

    internal void ResetSlot(int slot)
    {
        SetPreferences(slot, MineMode.All, OthersMode.All);
        SetTeam(slot, 0);
    }

    internal void ResetTeams()
    {
        Array.Clear(_teams);
        for (var slot = 0; slot < Slots; slot++) Refresh(slot);
    }

    private void Refresh(int slot)
    {
        var bit = Bit(slot);
        UInt128 hiddenFromSlot = 0;
        for (var other = 0; other < Slots; other++)
        {
            if (other == slot) continue;
            if (IsHidden(slot, other))
                _hidden[other] |= bit;
            else
                _hidden[other] &= ~bit;
            if (IsHidden(other, slot))
                hiddenFromSlot |= Bit(other);
        }
        _hidden[slot] = hiddenFromSlot;
    }

    private bool IsHidden(int owner, int viewer)
    {
        var teammates = _teams[owner] > 1 && _teams[owner] == _teams[viewer];
        return _mine[owner] == MineMode.Self || _others[viewer] == OthersMode.None ||
               (!teammates && (_mine[owner] == MineMode.Team || _others[viewer] == OthersMode.Team));
    }
}

internal sealed class TrailEntities
{
    private readonly record struct Entry(uint Handle, int Owner, bool Carried);
    private readonly record struct Expiration(uint Index, uint Handle);
    private readonly Dictionary<uint, Entry> _byIndex = new();
    private readonly HashSet<uint>?[] _byOwner = new HashSet<uint>?[TrailVisibility.Slots];
    private readonly PriorityQueue<Expiration, double> _expiration = new();
    private int _temporaryCount;

    internal UInt128 Owners { get; private set; }
    internal bool HasExpirations => _expiration.Count != 0;
    internal HashSet<uint>? Indices(int slot) => _byOwner[slot];

    internal void Add(int owner, uint index, uint handle, double? expiresAt)
    {
        if (_byIndex.TryGetValue(index, out var previous))
            Remove(index, previous.Handle);

        _byIndex.Add(index, new Entry(handle, owner, !expiresAt.HasValue));
        (_byOwner[owner] ??= new HashSet<uint>()).Add(index);
        Owners |= TrailVisibility.Bit(owner);
        if (expiresAt is { } deadline)
        {
            _temporaryCount++;
            _expiration.Enqueue(new Expiration(index, handle), deadline);
        }
    }

    internal void Remove(uint index, uint handle)
    {
        if (!_byIndex.TryGetValue(index, out var entry) || entry.Handle != handle) return;
        _byIndex.Remove(index);
        var indices = _byOwner[entry.Owner]!;
        indices.Remove(index);
        if (indices.Count == 0) Owners &= ~TrailVisibility.Bit(entry.Owner);
        if (!entry.Carried && --_temporaryCount == 0) _expiration.Clear();
    }

    internal bool TryTakeExpired(double now, out uint handle)
    {
        // Engine deletions can leave stale queue entries, including entries with long lifetimes.
        if (_expiration.Count > 2 * _temporaryCount + 64)
        {
            var live = _expiration.UnorderedItems.Where(item => IsCurrent(item.Element)).ToArray();
            _expiration.Clear();
            _expiration.EnqueueRange(live);
        }

        while (_expiration.TryPeek(out var next, out var deadline) && deadline <= now)
        {
            _expiration.Dequeue();
            if (!IsCurrent(next)) continue;
            handle = next.Handle;
            return true;
        }

        handle = default;
        return false;
    }

    internal IEnumerable<(uint Handle, bool Carried)> OwnedBy(int slot)
    {
        if (_byOwner[slot] is not { Count: > 0 } indices) yield break;
        foreach (var index in indices)
        {
            var entry = _byIndex[index];
            yield return (entry.Handle, entry.Carried);
        }
    }

    internal void Clear()
    {
        _byIndex.Clear();
        foreach (var indices in _byOwner) indices?.Clear();
        _expiration.Clear();
        _temporaryCount = 0;
        Owners = 0;
    }

    private bool IsCurrent(Expiration expiration) =>
        _byIndex.TryGetValue(expiration.Index, out var entry) && entry.Handle == expiration.Handle;
}
