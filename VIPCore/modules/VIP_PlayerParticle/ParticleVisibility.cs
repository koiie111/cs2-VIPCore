using System.Numerics;

namespace VIP_PlayerParticle;

internal enum MineMode { All, Team, Self }
internal enum OthersMode { All, Team, None }

internal sealed class ParticleVisibility
{
    // Include slot 64, which CounterStrikeSharp can expose on a 64-player server.
    internal const int Slots = 65;

    private readonly (bool Connected, int Team, MineMode Mine, OthersMode Others, bool SeeOwn)[] _viewers = new
        (bool, int, MineMode, OthersMode, bool)[Slots];
    private readonly UInt128[] _teams = new UInt128[byte.MaxValue + 1];
    private readonly int[] _entityIndices = new int[Slots];
    private readonly UInt128[] _hidden = new UInt128[Slots];
    private readonly UInt128[] _hiddenFrom = new UInt128[Slots];
    private UInt128 _owners;
    private UInt128 _teamOnlyOwners;
    private UInt128 _selfOnlyOwners;
    private UInt128 _connected;
    private UInt128 _teamOnlyViewers;
    private UInt128 _hideOthersViewers;
    private int _restrictedViewers;

    internal bool NeedsFilter => _restrictedViewers != 0;
    internal UInt128 Hidden(int viewer) => _hidden[viewer];
    internal int EntityIndex(int owner) => _entityIndices[owner];

    internal static int PopSlot(ref UInt128 slots)
    {
        var slot = (ulong)slots != 0
            ? BitOperations.TrailingZeroCount((ulong)slots)
            : 64 + BitOperations.TrailingZeroCount((ulong)(slots >> 64));
        slots &= slots - 1;
        return slot;
    }

    private static UInt128 Bit(int slot) => UInt128.One << slot;
    private static UInt128 SetBit(UInt128 mask, UInt128 bit, bool enabled) => enabled ? mask | bit : mask & ~bit;

    internal void SetViewer(int slot, bool connected, int team, MineMode mine, OthersMode others, bool seeOwn)
    {
        var next = (connected, team, mine, others, seeOwn);
        var previous = _viewers[slot];
        if (previous == next) return;
        _viewers[slot] = next;
        var bit = Bit(slot);
        _connected = SetBit(_connected, bit, connected);
        _teamOnlyViewers = SetBit(_teamOnlyViewers, bit, connected && others == OthersMode.Team);
        _hideOthersViewers = SetBit(_hideOthersViewers, bit, connected && others == OthersMode.None);
        if (previous.Connected && IsPlayingTeam(previous.Team))
            _teams[previous.Team] &= ~bit;
        if (connected && IsPlayingTeam(team))
            _teams[team] |= bit;

        if (_entityIndices[slot] != 0 && previous.Mine != mine)
            UpdateOwnerMode(slot);

        // Updating a recipient affects just its row. Updating an owner can affect every recipient.
        if (previous.Connected != connected || previous.Team != team || previous.Others != others)
            RefreshViewer(slot);
        if (_entityIndices[slot] != 0 &&
            (previous.Connected != connected || previous.Team != team || previous.Mine != mine || previous.SeeOwn != seeOwn))
            RefreshOwner(slot);
    }

    internal void SetParticle(int slot, int entityIndex)
    {
        if (_entityIndices[slot] == entityIndex) return;
        _entityIndices[slot] = entityIndex;
        var bit = Bit(slot);
        _owners = SetBit(_owners, bit, entityIndex != 0);
        UpdateOwnerMode(slot);
        RefreshOwner(slot);
    }

    private static bool IsPlayingTeam(int team) => team is > 1 and <= byte.MaxValue;

    private void UpdateOwnerMode(int slot)
    {
        var bit = Bit(slot);
        var mine = _viewers[slot].Mine;
        _teamOnlyOwners = SetBit(_teamOnlyOwners, bit, _entityIndices[slot] != 0 && mine == MineMode.Team);
        _selfOnlyOwners = SetBit(_selfOnlyOwners, bit, _entityIndices[slot] != 0 && mine == MineMode.Self);
    }

    private void RefreshViewer(int viewer)
    {
        var target = _viewers[viewer];
        var bit = Bit(viewer);
        var allies = IsPlayingTeam(target.Team) ? _teams[target.Team] : 0;
        UInt128 next = 0;
        if (target.Connected)
        {
            next = target.Others switch
            {
                OthersMode.None => _owners,
                OthersMode.Team => (_owners & ~allies) | _selfOnlyOwners,
                _ => (_teamOnlyOwners & ~allies) | _selfOnlyOwners,
            };
            next = SetBit(next, bit, (_owners & bit) != 0 && !target.SeeOwn);
        }

        var changed = next ^ _hidden[viewer];
        SetHidden(viewer, next);
        while (changed != 0)
            _hiddenFrom[PopSlot(ref changed)] ^= bit;
    }

    private UInt128 HiddenFrom(int owner)
    {
        var source = _viewers[owner];
        var self = Bit(owner);
        var allies = source.Connected && IsPlayingTeam(source.Team) ? _teams[source.Team] : 0;
        var hidden = source.Mine switch
        {
            MineMode.Team => _connected & ~allies,
            MineMode.Self => _connected,
            _ => _teamOnlyViewers & ~allies,
        };
        return SetBit(hidden | _hideOthersViewers, self, source.Connected && !source.SeeOwn);
    }

    private void RefreshOwner(int owner)
    {
        var next = _entityIndices[owner] == 0 ? 0 : HiddenFrom(owner);
        var changed = next ^ _hiddenFrom[owner];
        _hiddenFrom[owner] = next;
        var bit = Bit(owner);
        while (changed != 0)
        {
            var viewer = PopSlot(ref changed);
            SetHidden(viewer, _hidden[viewer] ^ bit);
        }
    }

    private void SetHidden(int viewer, UInt128 hidden)
    {
        if (_hidden[viewer] == 0 && hidden != 0) _restrictedViewers++;
        else if (_hidden[viewer] != 0 && hidden == 0) _restrictedViewers--;
        _hidden[viewer] = hidden;
    }

    internal void Clear()
    {
        Array.Clear(_viewers);
        Array.Clear(_teams);
        Array.Clear(_entityIndices);
        Array.Clear(_hidden);
        Array.Clear(_hiddenFrom);
        _owners = _teamOnlyOwners = _selfOnlyOwners = 0;
        _connected = _teamOnlyViewers = _hideOthersViewers = 0;
        _restrictedViewers = 0;
    }
}
