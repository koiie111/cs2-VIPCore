using System.Numerics;

namespace VIP_Postprocessing;

internal sealed class VolumeVisibility
{
    public const int Slots = 64;

    private readonly Dictionary<uint, int> _owners = new();
    private readonly uint[] _handles = new uint[Slots];
    private readonly uint[] _indices = new uint[Slots];
    private ulong _replaceMap;

    public Dictionary<uint, uint> Retiring { get; } = new();
    public Dictionary<uint, uint> Map { get; } = new();
    public ulong Active { get; private set; }
    public ulong Spectatable { get; private set; }
    public bool NeedsFilter => Active != 0 || Retiring.Count != 0;

    public uint Handle(int slot) => _handles[slot];
    public uint Index(int slot) => _indices[slot];
    public bool Owns(uint handle) => _owners.ContainsKey(handle);

    public void Add(int slot, uint handle, uint index, bool spectators, bool replaceMap)
    {
        _owners.Add(handle, slot);
        _handles[slot] = handle;
        _indices[slot] = index;
        Active |= 1UL << slot;
        SetPreferences(slot, spectators, replaceMap);
    }

    public void SetPreferences(int slot, bool spectators, bool replaceMap)
    {
        var bit = Active & (1UL << slot);
        Spectatable = spectators ? Spectatable | bit : Spectatable & ~bit;
        _replaceMap = replaceMap ? _replaceMap | bit : _replaceMap & ~bit;
    }

    public void Retire(uint handle, uint index)
    {
        Forget(handle);
        Retiring.Add(handle, index);
        _owners.Add(handle, -1);
    }

    // Returns the slot whose active volume was deleted, if any.
    public int Forget(uint handle)
    {
        Map.Remove(handle);
        if (!_owners.Remove(handle, out var slot)) return -1;
        if (slot < 0)
        {
            Retiring.Remove(handle);
            return -1;
        }

        SetPreferences(slot, false, false);
        Active &= ~(1UL << slot);
        _handles[slot] = 0;
        return slot;
    }

    public ulong HiddenOwners(int shown) => Active & ~OwnerBit(shown);
    public bool HideMap(int shown) => (_replaceMap & OwnerBit(shown)) != 0;

    public static ulong OwnerBit(int slot) => (uint)slot < Slots ? 1UL << slot : 0;

    public static int TakeSlot(ref ulong slots)
    {
        var slot = BitOperations.TrailingZeroCount(slots);
        slots &= slots - 1;
        return slot;
    }

    public void Clear()
    {
        _owners.Clear();
        Retiring.Clear();
        Map.Clear();
        Array.Clear(_handles);
        Active = Spectatable = _replaceMap = 0;
    }
}
