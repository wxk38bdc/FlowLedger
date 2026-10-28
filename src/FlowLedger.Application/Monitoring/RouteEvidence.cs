namespace FlowLedger.Application.Monitoring;

public sealed record InterfaceRouteEvidence(ulong Luid, int Ipv4DefaultRoutes, int Ipv6DefaultRoutes, int OtherPrefixRoutes);

public sealed record RouteEvidence(IReadOnlyList<InterfaceRouteEvidence> Interfaces, uint ErrorCode = 0);

public interface IRouteEvidenceReader
{
    RouteEvidence Read();
}
