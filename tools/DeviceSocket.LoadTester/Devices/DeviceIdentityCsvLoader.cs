using System.Globalization;

namespace DeviceSocket.LoadTester.Devices;

/// <summary>
/// 读取 <c>meterId,deviceId</c> CSV，并把真实老设备编号规范化为376.1协议身份。
/// </summary>
internal static class DeviceIdentityCsvLoader
{
    /// <summary>
    /// 读取并完整校验设备映射；空行和以 <c>#</c> 开头的说明行不参与全局序号计算。
    /// </summary>
    /// <param name="path">设备映射 CSV 路径。</param>
    /// <param name="allowZeroTerminalAddress">是否允许老 Gather 服务数据库中的特殊终端地址 <c>00000</c>；标准376.1场景必须保持 false。</param>
    /// <returns>按 CSV 数据行顺序排列的真实设备映射。</returns>
    /// <exception cref="InvalidDataException">列数、表头、主键、设备号或唯一性不符合要求。</exception>
    internal static IReadOnlyList<DeviceIdentityCsvEntry> Load(
        string path,
        bool allowZeroTerminalAddress = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var entries = new List<DeviceIdentityCsvEntry>();
        var seenMeterIds = new HashSet<int>();
        var seenDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        var firstContentLine = true;
        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var columns = line.Split(',', StringSplitOptions.TrimEntries);
            if (columns.Length != 2)
                throw new InvalidDataException($"设备映射 CSV 第 {lineNumber} 行必须只有 meterId,deviceId 两列。");
            if (firstContentLine && string.Equals(columns[0], "meterId", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(columns[1], "deviceId", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"设备映射 CSV 第 {lineNumber} 行表头必须是 meterId,deviceId。");
                firstContentLine = false;
                continue;
            }

            firstContentLine = false;
            if (!int.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var meterId) || meterId <= 0)
                throw new InvalidDataException($"设备映射 CSV 第 {lineNumber} 行 meterId 必须是正整数。");

            var identity = ParseDeviceIdentity(columns[1], lineNumber, allowZeroTerminalAddress);
            if (!seenMeterIds.Add(meterId))
                throw new InvalidDataException($"设备映射 CSV 存在重复 meterId：{meterId}。");
            // 同一个身份可能分别写成九位老编号和规范 DeviceId，必须在规范化后检查重复。
            if (!seenDeviceIds.Add(identity.DeviceId))
                throw new InvalidDataException($"设备映射 CSV 存在重复 deviceId：{identity.DeviceId}。");

            entries.Add(new DeviceIdentityCsvEntry(
                entries.Count,
                meterId,
                identity.DeviceId,
                identity.AdministrativeDivision,
                identity.TerminalAddress));
        }

        if (entries.Count == 0)
            throw new InvalidDataException("设备映射 CSV 不包含任何有效数据行。");
        return entries;
    }

    /// <summary>
    /// 解析九位老设备号或规范376.1 DeviceId，并统一补齐五位终端地址。
    /// </summary>
    /// <param name="value">CSV 中的原始 deviceId。</param>
    /// <param name="lineNumber">用于错误定位的 CSV 行号。</param>
    /// <param name="allowZeroTerminalAddress">是否接受老 Gather 服务真实存在的特殊终端地址 <c>00000</c>。</param>
    /// <returns>规范设备标识、行政区划和终端地址。</returns>
    /// <exception cref="InvalidDataException">设备号不满足四位区划加有效16位终端地址规则。</exception>
    private static ParsedDeviceIdentity ParseDeviceIdentity(
        string value,
        int lineNumber,
        bool allowZeroTerminalAddress)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("3761:", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[5..];

        string administrativeDivision;
        string terminalAddressText;
        var separatorIndex = normalized.IndexOf('-');
        if (separatorIndex >= 0)
        {
            if (separatorIndex != normalized.LastIndexOf('-'))
                throw InvalidDeviceId(lineNumber, value, allowZeroTerminalAddress);
            administrativeDivision = normalized[..separatorIndex];
            terminalAddressText = normalized[(separatorIndex + 1)..];
        }
        else
        {
            // 老库设备编号固定为前四位行政区划加后五位终端地址，不允许靠猜测拆分其他长度。
            if (normalized.Length != 9 || !normalized.All(char.IsDigit))
                throw InvalidDeviceId(lineNumber, value, allowZeroTerminalAddress);
            administrativeDivision = normalized[..4];
            terminalAddressText = normalized[4..];
        }

        if (administrativeDivision.Length != 4 || !administrativeDivision.All(char.IsDigit) ||
            string.IsNullOrEmpty(terminalAddressText) || !terminalAddressText.All(char.IsDigit) ||
            !ushort.TryParse(terminalAddressText, NumberStyles.None, CultureInfo.InvariantCulture, out var terminalAddress) ||
            (!allowZeroTerminalAddress && terminalAddress == 0))
        {
            throw InvalidDeviceId(lineNumber, value, allowZeroTerminalAddress);
        }

        return new ParsedDeviceIdentity(
            $"3761:{administrativeDivision}-{terminalAddress:D5}",
            administrativeDivision,
            terminalAddress);
    }

    /// <summary>
    /// 创建包含格式说明和源行号的设备编号异常。
    /// </summary>
    /// <param name="lineNumber">CSV 行号。</param>
    /// <param name="value">无效的原始设备号。</param>
    /// <param name="allowZeroTerminalAddress">是否启用了老 Gather 服务的零地址兼容。</param>
    /// <returns>可直接抛出的数据格式异常。</returns>
    private static InvalidDataException InvalidDeviceId(
        int lineNumber,
        string value,
        bool allowZeroTerminalAddress) => new(
        $"设备映射 CSV 第 {lineNumber} 行 deviceId“{value}”无效；必须是九位老编号（如260810001）或3761:2608-10001格式，终端地址范围为{(allowZeroTerminalAddress ? "0-65535（00000仅兼容Gather老服务）" : "1-65535")}。");

    /// <summary>
    /// 保存单个设备编号解析后的规范字段，避免调用方再次拆解文本。
    /// </summary>
    /// <param name="DeviceId">规范376.1设备标识。</param>
    /// <param name="AdministrativeDivision">四位行政区划。</param>
    /// <param name="TerminalAddress">16位终端地址。</param>
    private sealed record ParsedDeviceIdentity(
        string DeviceId,
        string AdministrativeDivision,
        ushort TerminalAddress);
}
