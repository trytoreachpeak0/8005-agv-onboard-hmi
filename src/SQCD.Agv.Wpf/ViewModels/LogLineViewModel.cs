using System.Globalization;
using SQCD.Agv.Application;

namespace SQCD.Agv.Wpf.ViewModels;

public sealed record LogLineViewModel(
    DateTimeOffset Timestamp,
    OperatorRecordKind Kind,
    string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string CategoryText => Kind switch
    {
        OperatorRecordKind.System => "系统",
        OperatorRecordKind.Operation => "操作",
        OperatorRecordKind.Success => "成功",
        OperatorRecordKind.Warning => "提醒",
        OperatorRecordKind.Error => "故障",
        _ => "记录"
    };
}
