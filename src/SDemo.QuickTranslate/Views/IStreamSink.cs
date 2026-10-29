namespace SDemo.QuickTranslate.Views;

/// <summary>
/// 流式输出的目标界面：正文/思考增量、状态文案、提示条都写到这里。
/// 「翻译结果浮窗」<see cref="PopupWindow"/> 与「输入翻译窗口」<see cref="InputTranslateWindow"/> 都实现该接口，
/// 因此 <see cref="Services.TranslateController"/> 里「逐字追加 + 最大输出 Token 截断提示」的逻辑只需要一份。
/// </summary>
public interface IStreamSink
{
    /// <summary>更新标题栏右侧的状态文案。</summary>
    void SetStatus(string s);

    /// <summary>显示本次请求实际使用的「API / 模型」（多 API 时方便确认走的是哪一个）。</summary>
    void SetModel(string providerModel);

    /// <summary>追加一段正文（译文）增量。</summary>
    void AppendResultChunk(string chunk);

    /// <summary>追加一段思考内容增量（实时显示）。</summary>
    void AppendReasoningChunk(string chunk);

    /// <summary>显示常驻提示条（如「已达最大输出 Token 上限」）。</summary>
    void SetNotice(string text);

    /// <summary>当前是否显示了提示条。</summary>
    bool HasNotice { get; }

    /// <summary>当前已显示的正文长度。</summary>
    int ResultLength { get; }
}
