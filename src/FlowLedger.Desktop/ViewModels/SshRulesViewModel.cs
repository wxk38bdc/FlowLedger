using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using FlowLedger.Desktop.Localization;
using FlowLedger.Domain.Ssh;

namespace FlowLedger.Desktop.ViewModels;

public sealed class SshRulesViewModel(HistoryViewModel history) : ObservableState
{
    private UiText status = L.T("未添加规则；新建出站连接仍可按客户端候选显示。", "No rules yet. New outbound connections can still appear as client candidates.");
    public ObservableCollection<SshServerRule> Rules { get; } = [];
    public IReadOnlyList<SshServerRule> CaptureRules { get; private set; } = [];
    public string Address { get; set; } = "";
    public string Port { get; set; } = "22";
    public string Alias { get; set; } = "";
    public SshServerRule? SelectedRule { get; set; }
    public string Status => status;

    public async Task LoadAsync()
    {
        try
        {
            var json = await history.ReadSettingAsync("ssh-rules");
            var rules = json is null ? [] : JsonSerializer.Deserialize<SshServerRule[]>(json) ?? [];
            if (rules.Length > 128 || rules.Any(rule => !Valid(rule)) || rules.Select(rule => (rule.Address, rule.Port)).Distinct().Count() != rules.Length)
            { throw new ArgumentException("Invalid stored rules."); }
            foreach (var rule in rules) { Rules.Add(rule); }
            CaptureRules = rules;
        }
        catch (Exception) { status = L.T("保存的规则无法读取；未使用这些规则。", "Stored rules are invalid and have not been used."); Notify(null); }
    }

    public async Task AddAsync()
    {
        var normalized = IPAddress.TryParse(Address.Trim(), out var ip) ? ip.ToString() : "";
        var rule = new SshServerRule(normalized, int.TryParse(Port, out var port) ? port : 0, Alias.Trim());
        if (!Valid(rule) || Rules.Count >= 128 || Rules.Any(item => item.Address == rule.Address && item.Port == rule.Port))
        { status = L.T("请输入唯一的 IPv4 / IPv6 地址、1–65535 端口及不超过 80 字的别名；最多 128 条规则。", "Enter a unique IPv4 / IPv6 address, port 1–65535 and an alias of 1–80 characters. Maximum 128 rules."); Notify(null); return; }
        var next = Rules.Append(rule).ToArray();
        if (await history.SetSettingAsync("ssh-rules", JsonSerializer.Serialize(next)))
        { CaptureRules = next; Rules.Add(rule); Address = ""; Alias = ""; status = L.T("规则已保存，下次开始生效。", "Rule saved. Applies to the next capture."); }
        else { status = L.T("规则未能保存。", "Rule could not be saved."); }
        Notify(null);
    }

    public async Task RemoveAsync()
    {
        if (SelectedRule is not { } rule) { return; }
        var next = Rules.Where(item => item != rule).ToArray();
        if (await history.SetSettingAsync("ssh-rules", JsonSerializer.Serialize(next)))
        { CaptureRules = next; Rules.Remove(rule); SelectedRule = null; status = L.T("规则已移除，下次开始生效。", "Rule removed. Applies to the next capture."); }
        else { status = L.T("规则未能移除。", "Rule could not be removed."); }
        Notify(null);
    }

    private static bool Valid(SshServerRule rule) => IPAddress.TryParse(rule.Address, out var ip) && ip.ToString() == rule.Address
        && (!rule.Address.Contains('%')) && rule.Port is >= 1 and <= 65535 && rule.Alias.Length is >= 1 and <= 80 && !rule.Alias.Any(char.IsControl);
}
