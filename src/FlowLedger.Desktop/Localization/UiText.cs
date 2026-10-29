namespace FlowLedger.Desktop.Localization;

public sealed record UiText(string Chinese, string English)
{
    public override string ToString() => L.English ? English : Chinese;
    public static implicit operator string(UiText value) => value.ToString();
    public static implicit operator UiText(string value) => new(value, value);
    public static UiText operator +(UiText left, UiText right) => new(left.Chinese + right.Chinese, left.English + right.English);
}

public static class L
{
    public static bool English { get; private set; }
    public static void UseEnglish(bool value) => English = value;
    public static UiText T(string chinese, string english) => new(chinese, english);
    public static string Bytes(ulong? value) => value is not { } bytes ? T("尚未测量", "Not measured") : bytes switch
    {
        >= 1_099_511_627_776 => $"{bytes / 1_099_511_627_776d:F2} TiB",
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:F2} GiB",
        >= 1_048_576 => $"{bytes / 1_048_576d:F2} MiB",
        >= 1024 => $"{bytes / 1024d:F2} KiB",
        _ => $"{bytes} B",
    };
}
