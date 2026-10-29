using FlowLedger.Collector.Validation;
using FlowLedger.Domain.Accounting;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class ApplicationValidationTests
{
    [TestMethod]
    [DataRow("0", "1", "1", "1", "Tcp4")]
    [DataRow("65536", "1", "1", "1", "Tcp4")]
    [DataRow("1234", "8388609", "1", "1", "Tcp4")]
    [DataRow("1234", "1", "1401", "1", "Udp6")]
    [DataRow("1234", "8388608", "8388608", "2", "Tcp6")]
    [DataRow("1234", "1", "1", "33", "Tcp4")]
    [DataRow("1234", "1", "1", "1", "0")]
    [DataRow("1234", "0", "1", "1", "Tcp4")]
    public void WorkerRejectsUnboundedOrInvalidTraffic(string port, string send, string receive, string exchanges, string transport) =>
        Assert.IsFalse(ProbeParameters.TryParse(["--app-probe", transport, port, send, receive, exchanges], out _));

    [TestMethod]
    public void WorkerAcceptsBoundedAsymmetricProbe()
    {
        Assert.IsTrue(ProbeParameters.TryParse(["--app-probe", "Udp6", "1234", "257", "127", "24"], out var parameters));
        var actual = parameters!.Expected;
        Assert.AreEqual(new TrafficVolume(3048, 6168), actual);
        Assert.IsTrue(System.Net.IPAddress.IsLoopback(parameters.Address));
    }

    [TestMethod]
    public void ReversedDirectionsFailEvenWhenTotalBytesMatch()
    {
        var parameters = new ProbeParameters(NetworkTransport.Tcp4, 1234, 100, 40, 1);
        var correct = default(TransportTraffic).Add(NetworkTransport.Tcp4, parameters.Expected);
        var reversed = default(TransportTraffic).Add(NetworkTransport.Tcp4, new(100, 40));
        var rows = new[] { new ProcessTraffic(7, 1, "synthetic", false, reversed) };
        var result = ApplicationValidation.Compare(1, "test", parameters, true, rows, correct, 1, 1);
        Assert.IsFalse(result.Passed);
        Assert.IsFalse(result.SameLayerMatch);
        Assert.IsFalse(result.SocketPayloadMatch);
    }

    [TestMethod]
    public void TwoMissingReferencesCannotPassAsExactZero()
    {
        var result = ApplicationValidation.Compare(1, "test", new(NetworkTransport.Udp4, 1234, 100, 40, 1),
            true, [], default, 1, 1);
        Assert.IsTrue(result.SameLayerMatch);
        Assert.IsFalse(result.SocketPayloadMatch);
        Assert.IsFalse(result.Passed);
    }

    [TestMethod]
    [DataRow(false, false, 1, 1)]
    [DataRow(true, true, 1, 1)]
    [DataRow(true, false, 0, 1)]
    [DataRow(true, false, 1, 0)]
    [DataRow(true, false, 2, 1)]
    public void ExactBytesStillRequireDeliveryAndUnambiguousLifetime(bool delivered, bool rundown, int starts, int stops)
    {
        var parameters = new ProbeParameters(NetworkTransport.Tcp6, 1234, 100, 40, 1);
        var traffic = default(TransportTraffic).Add(parameters.Transport, parameters.Expected);
        var result = ApplicationValidation.Compare(1, "test", parameters, delivered,
            [new(7, 1, "synthetic", rundown, traffic)], traffic, starts, stops);
        Assert.IsTrue(result.SocketPayloadMatch);
        Assert.IsFalse(result.Passed);
    }

    [TestMethod]
    public void ExtraTransportCannotHideBehindMatchingTargetBytes()
    {
        var parameters = new ProbeParameters(NetworkTransport.Tcp4, 1234, 100, 40, 1);
        var traffic = default(TransportTraffic).Add(parameters.Transport, parameters.Expected).Add(NetworkTransport.Udp4, new(1, 0));
        var result = ApplicationValidation.Compare(1, "test", parameters, true,
            [new(7, 1, "synthetic", false, traffic)], traffic, 1, 1);
        Assert.IsFalse(result.Passed);
    }

    [TestMethod]
    public void IsolatedNewLifetimeWithAllReferencesPasses()
    {
        var parameters = new ProbeParameters(NetworkTransport.Udp6, 1234, 257, 127, 24);
        var traffic = default(TransportTraffic).Add(parameters.Transport, parameters.Expected);
        var result = ApplicationValidation.Compare(1, "test", parameters, true,
            [new(7, 1, "synthetic", false, traffic)], traffic, 1, 1);
        Assert.IsTrue(result.Passed);
    }
}
