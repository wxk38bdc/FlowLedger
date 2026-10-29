using FlowLedger.Domain.Proxy;

namespace FlowLedger.Application.Proxy;

public enum ProxyReadState { Available, AccessDenied, Unavailable, InvalidResponse, CoreUnconfirmed }
public sealed record ProxyReadout(ProxyReadState State, string Version, ProxyObservation? Observation, int ErrorCode);
public interface IProxyTrafficSource : IAsyncDisposable
{
    Task<ProxyReadout> ReadAsync(CancellationToken token = default);
}
