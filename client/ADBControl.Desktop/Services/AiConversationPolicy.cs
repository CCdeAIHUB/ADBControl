using System.Text;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services;

public static class AiConversationPolicy
{
    public static string BuildDeviceContext(DeviceModel? currentDevice, IReadOnlyList<DeviceModel> knownDevices)
    {
        var builder = new StringBuilder();
        if (currentDevice is null)
            builder.Append("当前没有固定操作目标，但仍可查询全部设备。 ");
        else
            builder.Append($"当前详情设备：{DisplayName(currentDevice)} ({currentDevice.DeviceId})。 ");

        if (knownDevices.Count == 0)
            return builder.Append("设备清单为空。").ToString();

        builder.Append("已知设备：");
        for (var index = 0; index < knownDevices.Count; index++)
        {
            var device = knownDevices[index];
            if (index > 0)
                builder.Append("；");
            builder.Append($"{DisplayName(device)} [deviceId={device.DeviceId}, {(device.IsConnected ? "ADB 已连接" : "ADB 未连接")}, {(device.IsCompanionConnected ? "伴侣已连接" : "伴侣未连接")}]");
        }
        builder.Append("。查询清单或连接状态不需要先打开详情页；执行具体设备操作时必须明确 deviceId。");
        return builder.ToString();
    }

    public static string BuildInteractionRules(bool allowInteractiveChoices)
        => allowInteractiveChoices
            ? "需要用户在有限选项中决定时，可调用 ask_user_choice；它只支持单选或多选。需要用户输入文字时，不得调用该工具，应直接提问并结束本轮，由用户以新一轮对话回复。"
            : "当前执行环境不支持内联提问；需要补充信息时应说明所需信息并结束本轮。";

    private static string DisplayName(DeviceModel device)
        => string.IsNullOrWhiteSpace(device.DisplayName) ? device.DeviceId : device.DisplayName;
}
