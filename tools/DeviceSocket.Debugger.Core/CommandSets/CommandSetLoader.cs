using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.Core.CommandSets;

/// <summary>
/// 负责从 JSON 文件加载指令集，并把语法与语义错误转换为可展示结果。
/// </summary>
public sealed class CommandSetLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// 异步加载并校验一个 JSON 指令集文件。
    /// </summary>
    /// <param name="filePath">JSON 文件绝对或相对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>包含成功指令集或精确错误信息的结果。</returns>
    public async Task<CommandSetLoadResult> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var definition = await JsonSerializer.DeserializeAsync<CommandSetDefinition>(
                stream,
                SerializerOptions,
                cancellationToken);
            if (definition is null)
                return new CommandSetLoadResult(filePath, null, ["JSON 文件内容为空。"]);

            var errors = CommandSetValidator.Validate(definition);
            return new CommandSetLoadResult(filePath, definition, errors);
        }
        catch (JsonException exception)
        {
            var location = exception.LineNumber.HasValue
                ? $"第 {exception.LineNumber + 1} 行、第 {exception.BytePositionInLine.GetValueOrDefault() + 1} 列"
                : "未知位置";
            return new CommandSetLoadResult(
                filePath,
                null,
                [$"JSON 格式错误（{location}，路径 {exception.Path ?? "-"}）：{exception.Message}"]);
        }
        catch (IOException exception)
        {
            return new CommandSetLoadResult(filePath, null, [$"读取文件失败：{exception.Message}"]);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new CommandSetLoadResult(filePath, null, [$"没有读取权限：{exception.Message}"]);
        }
    }
}
