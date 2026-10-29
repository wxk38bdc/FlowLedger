using FlowLedger.Infrastructure.Windows.Monitoring;

namespace FlowLedger.Windows.Tests;

[TestClass]
public sealed class EtwSessionOwnershipTests
{
    [TestMethod]
    public void OnlyExactPerUserNamespaceAndKnownKindsAreEligible()
    {
        var owner = Guid.NewGuid();
        var valid = EtwSessionOwnership.Format(owner, "App", 7, 123, Guid.NewGuid());
        Assert.IsTrue(EtwSessionOwnership.TryParse(valid, owner, out var pid, out var ticks));
        Assert.AreEqual(7, pid);
        Assert.AreEqual(123L, ticks);
        foreach (var invalid in new[] { valid + ".extra", valid.Replace(".App.", ".Other.", StringComparison.Ordinal),
            valid.Replace(".7.", ".0.", StringComparison.Ordinal), "FlowLedger.App." + Guid.NewGuid().ToString("N") })
        {
            Assert.IsFalse(EtwSessionOwnership.TryParse(invalid, owner, out _, out _));
        }

        Assert.IsFalse(EtwSessionOwnership.TryParse(valid, Guid.NewGuid(), out _, out _));
    }

    [TestMethod]
    public void ActiveUncertainForeignAndLegacySessionsAreNeverStopped()
    {
        var owner = Guid.NewGuid();
        string Name(int pid) => EtwSessionOwnership.Format(owner, "App", pid, 123, Guid.NewGuid());
        var dead = Name(1);
        var names = new[] { dead, Name(2), Name(3), "NT Kernel Logger",
            EtwSessionOwnership.Format(Guid.NewGuid(), "App", 4, 123, Guid.NewGuid()), "FlowLedger.App.legacy" };
        var stopped = new List<string>();
        var result = EtwSessionOwnership.Recover(names, owner, (pid, _) => pid switch { 1 => false, 2 => true, _ => null },
            name => { stopped.Add(name); return true; });
        CollectionAssert.AreEqual(new[] { dead }, stopped);
        Assert.AreEqual(1, result.Recovered);
        Assert.AreEqual(1, result.Unconfirmed);
        Assert.AreEqual(0, result.Failed);
    }

    [TestMethod]
    public void FailedRecoveryIsVisibleAndDoesNotStopOtherNames()
    {
        var owner = Guid.NewGuid();
        var name = EtwSessionOwnership.Format(owner, "Reference", 7, 123, Guid.NewGuid());
        var result = EtwSessionOwnership.Recover([name], owner, (_, _) => false, _ => throw new IOException());
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(0, result.Recovered);
    }
}
