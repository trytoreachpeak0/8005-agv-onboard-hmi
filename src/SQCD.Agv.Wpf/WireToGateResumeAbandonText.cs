namespace SQCD.Agv.Wpf;

/// <summary>The words of 「不装了」 (8005-agv-onboard-hmi#410), the one place they are written.</summary>
public static class WireToGateResumeAbandonText
{
    /// <summary>The button's caption, and the name the resume's prompt gives it.</summary>
    public const string Caption = "不装了";

    /// <summary>The confirmation the press asks: what ends, and what settles the load next.</summary>
    public const string Confirmation = "确定不装了吗？\n\n本次续行到此结束，不会再开门。之后请按「补偿清空」结束这次装货。";

    /// <summary>The answer when the press came after the offer had gone.</summary>
    public const string Refused = "当前状态已经变化，不能结束续行。请按照界面最新提示操作。";
}
