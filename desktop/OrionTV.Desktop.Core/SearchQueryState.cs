namespace OrionTV.Desktop.Core;

/// <summary>
/// 顶栏搜索框与搜索页输入框共用的查询状态：两处显示同一个关键词，改一处另一处跟着变。
/// 只是界面状态（内存里），不落盘、不参与搜索逻辑。
/// </summary>
public sealed class SearchQueryState
{
    /// <summary>当前关键词，原样保存（含首尾空白），永不为 null。</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>关键词真的变了才触发；设成相同内容不触发，两个输入框互相同步时不会来回打转。</summary>
    public event Action<string>? Changed;

    /// <summary>提交搜索用的关键词：去掉首尾空白；全是空白时为空串（表示没有关键词）。</summary>
    public string SubmittedQuery => Text.Trim();

    /// <summary>更新关键词。返回是否发生了变化。</summary>
    public bool Set(string? text)
    {
        var value = text ?? string.Empty;
        if (string.Equals(value, Text, StringComparison.Ordinal))
        {
            return false;
        }

        Text = value;
        Changed?.Invoke(value);
        return true;
    }
}
