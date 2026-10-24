namespace MineLib.Core;

/// <summary>
/// 给用户看的提示只讲两件事：发生了什么、怎么重试。
/// 原因、URL、文件名、异常原文一律进日志，不上界面。
/// 统一格式：在&lt;动作&gt;时出现错误，&lt;重试方式&gt;
/// </summary>
public static class Notice
{
    public static string Error(string action, string retry) => "在" + action + "时出现错误，" + retry;
}
