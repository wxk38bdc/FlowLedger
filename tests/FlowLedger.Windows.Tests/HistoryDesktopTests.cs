using FlowLedger.Application.History;
using FlowLedger.Application.Monitoring;
using FlowLedger.Desktop.Localization;
using FlowLedger.Desktop.ViewModels;
using FlowLedger.Domain.Accounting;
using FlowLedger.Infrastructure.Windows.History;

namespace FlowLedger.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryDesktopTests
{
    [TestMethod]
    public async Task FailedSaveRetriesLatestCumulativeWithoutDuplicateUsage()
    {
        var fake = new FakeStore { FailAppend = true };
        var model = new HistoryViewModel(fake, "synthetic"); await model.InitializeAsync();
        var epoch = Guid.NewGuid();
        ApplicationTrafficSnapshot Snapshot(ulong bytes) => new(epoch, ApplicationCaptureState.Running,
            new([new(1, 2, "ssh.exe", false, new(new(bytes, 0), default, default, default))], default,
                new(new(bytes, 0), default, default, default), 1, 0, 0, false, false), true, 0);
        await model.SaveApplicationsAsync(Snapshot(100)); Assert.IsTrue(model.HasUnsaved);
        fake.FailAppend = false;
        await model.SaveApplicationsAsync(Snapshot(150));
        Assert.IsFalse(model.HasUnsaved);
        Assert.HasCount(1, fake.Saved); Assert.AreEqual(150UL, fake.Saved[0].Observed!.Value.ReceivedBytes);
        StringAssert.Contains(fake.Saved[0].Quality, "StorageGap");
    }

    [TestMethod]
    public async Task OfflineInterfaceDoesNotCreateMeasuredZeroAndScopeNamesAreReadable()
    {
        var fake = new FakeStore(); var model = new HistoryViewModel(fake, "synthetic"); await model.InitializeAsync();
        var counter = new InterfaceCounter("synthetic-id", "Synthetic adapter", InterfaceKind.Hardware, 6, false, true, true, "one",
            new("synthetic-id", Guid.NewGuid(), DateTimeOffset.UtcNow, new(100, 50)));
        await model.SaveInterfacesAsync(new(DateTimeOffset.UtcNow, [new(counter, new(TrafficSampleStatus.Offline, null, new(100, 50), null, null, 1), true)], 0), new Dictionary<string, WifiLedgerSnapshot>());
        Assert.IsNull(fake.Saved.Single().Observed); Assert.AreEqual("Synthetic adapter", fake.Saved.Single().StreamLabel);
    }

    [TestMethod]
    public async Task RuleAndLanguageSettingsSurviveLocalDatabaseReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"FlowLedger-desktop-test-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var history = new HistoryViewModel(new SqliteHistoryStore(path), path); await history.InitializeAsync();
            var rules = new SshRulesViewModel(history) { Address = "::1", Port = "2222", Alias = "Synthetic server" };
            await rules.AddAsync(); Assert.HasCount(1, rules.Rules);
            Assert.IsTrue(await history.SetSettingAsync("language", "en"));
            var reopened = new HistoryViewModel(new SqliteHistoryStore(path), path); await reopened.InitializeAsync();
            var loaded = new SshRulesViewModel(reopened); await loaded.LoadAsync();
            Assert.AreEqual(rules.Rules[0], loaded.Rules.Single());
            Assert.AreEqual("en", await reopened.ReadSettingAsync("language"));
            rules.Address = "::1"; rules.Alias = "Duplicate"; await rules.AddAsync(); Assert.HasCount(1, rules.Rules);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) { File.Delete(path + suffix); } }
    }

    [TestMethod]
    public void LanguageSwitchUpdatesExistingPhraseAndSelectorLabels()
    {
        try
        {
            L.UseEnglish(false); var phrase = L.T("尚未启用", "Not started");
            var period = new PeriodChoice(ReportPeriod.Week, L.T("按周", "Weekly"));
            Assert.AreEqual("尚未启用", phrase.ToString());
            L.UseEnglish(true); Assert.AreEqual("Not started", phrase.ToString()); Assert.AreEqual("Weekly", period.Label);
            Assert.IsTrue(UiCatalog.Entries.Values.All(item => !string.IsNullOrWhiteSpace(item.English) && !string.IsNullOrWhiteSpace(item.Chinese)));
        }
        finally { L.UseEnglish(false); }
    }

    [TestMethod]
    public void CsvQuotesNamesAndNeutralizesSpreadsheetFormulas()
    {
        Assert.AreEqual("\"'=HYPERLINK(\"\"synthetic\"\")\"", HistoryViewModel.Csv("=HYPERLINK(\"synthetic\")"));
        Assert.AreEqual("\"a,\"\"b\"\"\r\nc\"", HistoryViewModel.Csv("a,\"b\"\r\nc"));
    }

    [TestMethod]
    public async Task StopPersistsFinalDrainedSnapshotBeforeDisposingSource()
    {
        var source = new FinalSource(); var saved = new List<ApplicationTrafficSnapshot>();
        var model = new ApplicationMonitorViewModel(() => source, persist: snapshot => { saved.Add(snapshot); return Task.CompletedTask; });
        await model.StartAsync(); await model.StopAsync();
        Assert.AreEqual(100UL, saved[0].Ledger.Observed!.Value.Total.ReceivedBytes);
        Assert.AreEqual(200UL, saved[^1].Ledger.Observed!.Value.Total.ReceivedBytes);
        Assert.IsTrue(source.Disposed); Assert.AreEqual(ApplicationCaptureState.Stopped, saved[^1].State);
    }

    [TestMethod]
    public async Task CaptureSaveStatusDoesNotOverwriteReportValidationError()
    {
        var model = new HistoryViewModel(new FakeStore(), "synthetic"); await model.InitializeAsync();
        model.SelectedStream = new(new("applications", HistoryLayer.Application));
        model.FromDate = new(2026, 2, 1); model.UntilDate = new(2026, 1, 1);
        await model.QueryAsync(); var error = model.ReportStatus;
        await model.SaveAsync([]);
        Assert.AreEqual(error, model.ReportStatus); Assert.IsFalse(model.CanExport);
    }

    [TestMethod]
    public async Task BufferedAutosaveCoalescesTotalsButPreservesQualityUntilExplicitFlush()
    {
        var store = new FakeStore(); var model = new HistoryViewModel(store, "synthetic", TimeSpan.FromHours(1)); await model.InitializeAsync();
        var now = DateTimeOffset.UtcNow; var epoch = Guid.NewGuid();
        HistoryFrame Frame(long sequence, ulong bytes, string quality) => new(Guid.NewGuid(), epoch, "applications", HistoryLayer.Application,
            sequence, now, now.AddSeconds(sequence), new(bytes, 0), [new("app", "Synthetic", new(bytes, 0))], quality);
        await model.SaveAsync([Frame(0, 10, "")]);
        await model.SaveAsync([Frame(1, 20, "Boundary")]); await model.SaveAsync([Frame(2, 30, "")]);
        Assert.HasCount(1, store.Saved); Assert.IsTrue(model.HasUnsaved);
        await model.SaveAsync([]);
        Assert.HasCount(2, store.Saved); Assert.AreEqual(30UL, store.Saved[^1].Observed!.Value.ReceivedBytes);
        StringAssert.Contains(store.Saved[^1].Quality, "Boundary"); Assert.IsFalse(model.HasUnsaved);
    }

    [TestMethod]
    public async Task MissingLatestFrameCannotEraseBufferedMeasuredBytes()
    {
        var store = new FakeStore(); var model = new HistoryViewModel(store, "synthetic", TimeSpan.FromHours(1)); await model.InitializeAsync();
        var now = DateTimeOffset.UtcNow; var epoch = Guid.NewGuid();
        HistoryFrame Frame(long sequence, ulong? bytes) => new(Guid.NewGuid(), epoch, "interface", HistoryLayer.Interface,
            sequence, now, now.AddSeconds(sequence), bytes is { } n ? new(n, 0) : null,
            bytes is { } m ? [new("interface", "Synthetic", new(m, 0))] : [], bytes is null ? "Offline" : "");
        await model.SaveAsync([Frame(0, null)]); await model.SaveAsync([Frame(1, 25)]); await model.SaveAsync([Frame(2, null)]);
        await model.SaveAsync([]);
        Assert.HasCount(3, store.Saved); Assert.AreEqual(25UL, store.Saved[1].Observed!.Value.ReceivedBytes);
        Assert.IsNull(store.Saved[2].Observed); Assert.IsTrue(store.Saved[1].Sequence < store.Saved[2].Sequence);
    }

    [TestMethod]
    public async Task RepeatedRuleClicksCannotRaceThePendingSettingsWrite()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeStore { SettingWrite = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException(); } } };
        var history = new HistoryViewModel(store, "synthetic"); await history.InitializeAsync();
        var rules = new SshRulesViewModel(history) { Address = "127.0.0.1", Port = "2222", Alias = "Synthetic" };
        var first = rules.AddAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.IsFalse(rules.CanChange);
            await rules.AddAsync(); await rules.RemoveAsync();
        }
        finally { release.Set(); await first; }
        Assert.HasCount(1, rules.Rules); Assert.HasCount(1, rules.CaptureRules); Assert.IsTrue(rules.CanChange);
    }

    private sealed class FinalSource : IApplicationTrafficSource
    {
        private readonly Guid epoch = Guid.NewGuid();
        private bool stopped;
        public bool Disposed { get; private set; }
        public ApplicationTrafficSnapshot Read()
        {
            Assert.IsFalse(Disposed);
            var bytes = stopped ? 200UL : 100UL;
            var traffic = new TransportTraffic(new(bytes, 0), default, default, default);
            return new(epoch, stopped ? ApplicationCaptureState.Stopped : ApplicationCaptureState.Running,
                new([new(1, 1, "ssh.exe", false, traffic)], default, traffic, 1, 0, 0, false, false), true, 0);
        }
        public Task StopAsync() { stopped = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeStore : IHistoryStore
    {
        public bool FailAppend { get; set; }
        public List<HistoryFrame> Saved { get; } = [];
        public Action? SettingWrite { get; init; }
        public void Initialize() { }
        public void Append(IReadOnlyList<HistoryFrame> frames) { if (FailAppend) { throw new IOException(); } Saved.AddRange(frames); }
        public IReadOnlyList<HistoryStream> GetStreams() => [];
        public HistoryReadout Read(HistoryQuery query) => new([], 0);
        public string? GetSetting(string key) => null;
        public void SetSetting(string key, string value) { SettingWrite?.Invoke(); }
    }
}
