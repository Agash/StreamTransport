namespace Agash.StreamTransport.Tests;

// What a test source hands frames through: once Close returns no frame is being handed over and none
// follows, so a connection ends at once without waiting for the source's loop to notice.
internal sealed class DeliveryGate
{
    private readonly Lock _lock = new();
    private bool _closed;

    // Holds the gate for one handover; open is false once the gate is closed.
    public Lock.Scope Enter(out bool open)
    {
        Lock.Scope scope = _lock.EnterScope();
        open = !_closed;
        return scope;
    }

    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
        }
    }
}
