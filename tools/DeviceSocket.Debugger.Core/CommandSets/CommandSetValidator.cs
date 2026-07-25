using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.Core.CommandSets;

/// <summary>
/// 对反序列化后的指令集执行协议和字段级语义校验。
/// </summary>
public static class CommandSetValidator
{
    private static readonly HashSet<string> SupportedProtocols =
        new(StringComparer.OrdinalIgnoreCase) { "QGdw3761", "Dlt6452007" };

    private static readonly HashSet<string> SupportedModes =
        new(StringComparer.OrdinalIgnoreCase) { "codec", "raw", "layout" };

    private static readonly HashSet<string> SupportedInputTypes =
        new(StringComparer.OrdinalIgnoreCase) { "text", "number", "hex", "select", "datetime" };

    private static readonly HashSet<string> SupportedLayoutSources =
        new(StringComparer.OrdinalIgnoreCase) { "fixed", "input", "computed", "copy" };

    /// <summary>
    /// 校验指令集的必填项、唯一性和当前版本支持范围。
    /// </summary>
    /// <param name="definition">待校验的指令集。</param>
    /// <returns>可直接展示给用户的错误集合。</returns>
    public static IReadOnlyList<string> Validate(CommandSetDefinition definition)
    {
        var errors = new List<string>();
        if (definition.Version != 1)
            errors.Add($"version: 当前只支持版本 1，实际为 {definition.Version}。");
        if (string.IsNullOrWhiteSpace(definition.Name))
            errors.Add("name: 指令集名称不能为空。");
        if (!SupportedProtocols.Contains(definition.Protocol))
            errors.Add($"protocol: 不支持协议“{definition.Protocol}”。");
        if (definition.DefaultPort is <= 0 or > 65535)
            errors.Add("defaultPort: 必须是 1-65535。");
        if (definition.Groups.Count == 0)
            errors.Add("groups: 至少需要一个指令分组。");

        var commandIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var groupIndex = 0; groupIndex < definition.Groups.Count; groupIndex++)
        {
            var group = definition.Groups[groupIndex];
            var groupPath = $"groups[{groupIndex}]";
            if (string.IsNullOrWhiteSpace(group.Name))
                errors.Add($"{groupPath}.name: 分组名称不能为空。");

            for (var commandIndex = 0; commandIndex < group.Commands.Count; commandIndex++)
            {
                var command = group.Commands[commandIndex];
                ValidateCommand(command, $"{groupPath}.commands[{commandIndex}]", commandIds, errors);
            }
        }

        return errors;
    }

    private static void ValidateCommand(
        CommandDefinition command,
        string path,
        HashSet<string> commandIds,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(command.Id))
            errors.Add($"{path}.id: 指令标识不能为空。");
        else if (!commandIds.Add(command.Id))
            errors.Add($"{path}.id: 指令标识“{command.Id}”重复。");
        if (string.IsNullOrWhiteSpace(command.Name))
            errors.Add($"{path}.name: 指令名称不能为空。");
        if (!SupportedModes.Contains(command.Mode))
            errors.Add($"{path}.mode: 只支持 codec 或 raw。");
        if (command.Mode.Equals("codec", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(command.Encoder))
            errors.Add($"{path}.encoder: codec 模式必须指定编码器。");
        if (command.Mode.Equals("raw", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(command.RawHex))
            errors.Add($"{path}.rawHex: raw 模式必须配置原始报文。");
        if (command.Mode.Equals("layout", StringComparison.OrdinalIgnoreCase))
            ValidateLayout(command.Layout, path, errors);
        if (command.Response.TimeoutMs is <= 0 or > 600_000)
            errors.Add($"{path}.response.timeoutMs: 必须是 1-600000 毫秒。");

        var inputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixedValue in command.FixedValues)
        {
            if (string.IsNullOrWhiteSpace(fixedValue.Key))
                errors.Add($"{path}.fixedValues: 固定参数键名不能为空。");
        }

        for (var inputIndex = 0; inputIndex < command.Inputs.Count; inputIndex++)
        {
            var input = command.Inputs[inputIndex];
            var inputPath = $"{path}.inputs[{inputIndex}]";
            if (string.IsNullOrWhiteSpace(input.Key))
                errors.Add($"{inputPath}.key: 参数键名不能为空。");
            else if (!inputKeys.Add(input.Key))
                errors.Add($"{inputPath}.key: 参数键名“{input.Key}”重复。");
            else if (command.FixedValues.Keys.Contains(input.Key, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{inputPath}.key: 参数“{input.Key}”不能同时配置为固定参数和界面输入。");
            if (string.IsNullOrWhiteSpace(input.Label))
                errors.Add($"{inputPath}.label: 参数标签不能为空。");
            if (!SupportedInputTypes.Contains(input.Type))
                errors.Add($"{inputPath}.type: 不支持输入类型“{input.Type}”。");
            if (input.Type.Equals("select", StringComparison.OrdinalIgnoreCase) && input.Options.Count == 0)
                errors.Add($"{inputPath}.options: select 类型至少需要一个候选值。");
        }
    }

    private static void ValidateLayout(
        CommandLayoutDefinition? layout,
        string path,
        List<string> errors)
    {
        if (layout is null || layout.Fields.Count == 0)
        {
            errors.Add($"{path}.layout.fields: layout 模式至少需要一个字段。");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < layout.Fields.Count; index++)
        {
            var field = layout.Fields[index];
            var fieldPath = $"{path}.layout.fields[{index}]";
            if (string.IsNullOrWhiteSpace(field.Name))
                errors.Add($"{fieldPath}.name: 字段名称不能为空。");
            else if (!names.Add(field.Name))
                errors.Add($"{fieldPath}.name: 字段名称“{field.Name}”重复。");
            if (!SupportedLayoutSources.Contains(field.Source))
                errors.Add($"{fieldPath}.source: 只支持 fixed、input、computed 或 copy。");
            if ((field.Source.Equals("computed", StringComparison.OrdinalIgnoreCase) ||
                 field.Source.Equals("copy", StringComparison.OrdinalIgnoreCase)) &&
                field.Size is < 1 or > 8)
                errors.Add($"{fieldPath}.size: 计算或复制字段长度必须是 1-8。");
        }
    }
}
