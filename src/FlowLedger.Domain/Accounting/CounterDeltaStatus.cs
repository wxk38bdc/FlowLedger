namespace FlowLedger.Domain.Accounting;

public enum CounterDeltaStatus
{
    BaselineEstablished,
    Measured,
    InterfaceChanged,
    EpochChanged,
    CounterDecreased,
    TimeNotIncreasing,
}
