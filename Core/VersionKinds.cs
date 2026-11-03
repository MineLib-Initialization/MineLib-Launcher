using System.Text.RegularExpressions;

namespace MineLib.Core;

/// <summary>客户端版本归类：正式版 / 快照 / 趣味版（愚人节）/ 远古版本。</summary>
public enum VersionKind
{
    Release,
    Snapshot,
    Fun,
    Ancient,
    Other
}

/// <summary>
/// 版本归类规则。官方清单里的 <c>type</c> 字段不够用：
/// 愚人节版本一律被标成 snapshot，早期正式版（1.7 / 1.6 这种）又被标成 snapshot，
/// 所以这里以版本号本身的命名为主、清单 type 为辅。
/// </summary>
public static class VersionKinds
{
    /// <summary>
    /// 官方愚人节/趣味版本。它们的 id 有时和普通快照长得一模一样（15w14a、26w14a），
    /// 只能点名；其余奇形怪状的 id 会被下面的兜底规则收进趣味版。
    /// 新增愚人节版本时往这里加一条即可。
    /// </summary>
    private static readonly HashSet<string> KnownFun = new(StringComparer.OrdinalIgnoreCase)
    {
        // 2013 年的 2.0 只在当年发给过主播，官方启动器清单里没有它；
        // 留着是防止哪天清单补上（补上就是愚人节版本）。
        "2.0",
        "15w14a",                 // 2015  爱之拥抱更新（id 长得跟每周快照一样，只能点名）
        "1.RV-Pre1",              // 2016  潮流更新
        "3D Shareware v1.34",     // 2019
        "20w14infinite",          // 2020  无限维度
        "22w13oneblockatatime",   // 2022  一块方块
        "23w13a_or_b",            // 2023  选票更新
        "24w14potato",            // 2024  毒土豆
        "25w14craftmine",         // 2025
        "26w14a"                  // 2026  （id 也长得像每周快照）
    };

    /// <summary>正式版：纯数字点号，例如 1.21 / 1.21.11 / 26.3。</summary>
    private static readonly Regex ReleaseId = new(@"^\d+(\.\d+)+$", RegexOptions.Compiled);

    /// <summary>预发布/候选：1.21.11-rc3、26.1-pre-2、26.4-snapshot-6、1.14.2 Pre-Release 4。</summary>
    private static readonly Regex PreviewId = new(
        @"^\d+(\.\d+)+[-\s]*(Pre-Release|pre|rc|snapshot)[-\s]*\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>每周快照：25w46a。</summary>
    private static readonly Regex WeeklyId = new(@"^\d{2}w\d{2}[a-z]$", RegexOptions.Compiled);

    public static VersionKind Classify(string? id, string? manifestType)
    {
        var name = (id ?? "").Trim();
        var type = (manifestType ?? "").Trim();
        if (name.Length == 0) return VersionKind.Other;

        if (KnownFun.Contains(name)) return VersionKind.Fun;
        // 早期正式版（1.7 / 1.6 / 1.4.3）在清单里被标成 snapshot，按版本号认回来
        if (ReleaseId.IsMatch(name)) return VersionKind.Release;
        if (PreviewId.IsMatch(name) || WeeklyId.IsMatch(name)) return VersionKind.Snapshot;
        if (type is "old_beta" or "old_alpha") return VersionKind.Ancient;
        if (type == "release") return VersionKind.Release;
        // 剩下的非标准命名快照基本都是愚人节版本（potato / craftmine / oneblock…）
        return VersionKind.Fun;
    }

    public static string Label(VersionKind kind) => kind switch
    {
        VersionKind.Release => "正式版",
        VersionKind.Snapshot => "快照",
        VersionKind.Fun => "趣味版",
        VersionKind.Ancient => "远古",
        _ => "其他"
    };

    /// <summary>点名清单（自检用来核对：清单里出现的愚人节版本必须都被归到趣味版）。</summary>
    public static IReadOnlyCollection<string> KnownFunIds => KnownFun;

    /// <summary>给筛选框用的固定顺序。</summary>
    public static readonly string[] FilterNames = { "正式版", "快照", "趣味版", "远古", "全部" };
}
