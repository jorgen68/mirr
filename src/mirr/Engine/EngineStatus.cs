namespace mirr.Engine;

public enum EngineState
{
    LocatingEngine,
    EngineMissing,
    WaitingForDevice,
    Preparing,
    Connecting,
    WaitingForTrust,
    Streaming,
    Stalled,
    Failed,
    Disconnected,
    Reconnecting,
}

public sealed record EngineStatus(EngineState State, string Detail, string DeviceLabel = "")
{
    public static readonly EngineStatus Locating = new(EngineState.LocatingEngine, "Searching for the pymobiledevice3 engine");
    public static readonly EngineStatus Missing = new(EngineState.EngineMissing, "pymobiledevice3 is not installed");
    public static readonly EngineStatus Waiting = new(EngineState.WaitingForDevice, "Connect an iPhone over USB");
    public static readonly EngineStatus Disconnected = new(EngineState.Disconnected, "iPhone disconnected");
    public static readonly EngineStatus Reconnecting = new(EngineState.Reconnecting, "Reconnecting…");
}
