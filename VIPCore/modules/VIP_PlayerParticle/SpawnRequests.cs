namespace VIP_PlayerParticle;

internal sealed class SpawnRequests
{
    private readonly (ulong Request, uint Pawn)[] _pending = new (ulong, uint)[ParticleVisibility.Slots];
    private ulong _lastRequest;

    internal bool TryQueue(int slot, uint pawn, out ulong request)
    {
        var pending = _pending[slot];
        if (pending.Request != 0 && pending.Pawn == pawn)
        {
            request = pending.Request;
            return false;
        }

        request = ++_lastRequest;
        _pending[slot] = (request, pawn);
        return true;
    }

    internal bool Take(int slot, ulong request)
    {
        if (_pending[slot].Request != request) return false;
        Cancel(slot);
        return true;
    }

    internal void Cancel(int slot) => _pending[slot] = default;
    internal void Clear() => Array.Clear(_pending);
}
