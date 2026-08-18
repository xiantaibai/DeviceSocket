using System.Reflection;
using System.Text.Json;
using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Tests.Configuration;

/// <summary>
/// 验证 LoadTester JSON 配置基准文档覆盖全部可序列化属性和枚举值，防止代码演进后使用说明失效。
/// </summary>
public sealed class ConfigurationDocumentationTests
{
    /// <summary>
    /// 验证每个配置属性的完整 JSON 路径以及每个枚举值都已在字段基准文档中登记。
    /// </summary>
    [Fact]
    public void EverySerializableConfigurationField_IsDocumented()
    {
        var documentationPath = Path.Combine(AppContext.BaseDirectory, "Docs", "json-configuration.md");
        Assert.True(File.Exists(documentationPath), $"配置基准文档不存在：{documentationPath}");
        var content = File.ReadAllText(documentationPath);

        var configurationTypes = EnumerateConfigurationTypes().ToArray();
        var missingPaths = EnumerateConfigurationPaths(typeof(LoadTestOptions), prefix: null)
            .Where(path => !content.Contains($"`{path}`", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var missingEnumValues = configurationTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Select(property => UnwrapNullable(property.PropertyType))
            .Where(type => type.IsEnum)
            .Distinct()
            .SelectMany(type => Enum.GetNames(type))
            .Select(JsonNamingPolicy.CamelCase.ConvertName)
            .Distinct(StringComparer.Ordinal)
            .Where(value => !content.Contains($"`{value}`", StringComparison.Ordinal))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missingPaths.Length == 0 && missingEnumValues.Length == 0,
            $"配置文档缺少字段路径：{string.Join(", ", missingPaths)}；缺少枚举值：{string.Join(", ", missingEnumValues)}");
    }

    /// <summary>
    /// 从根配置开始递归枚举所有 Options 类型，包含列表元素类型，避免新增嵌套配置对象时漏检。
    /// </summary>
    /// <returns>参与 JSON 反序列化的全部强类型配置模型。</returns>
    private static IEnumerable<Type> EnumerateConfigurationTypes()
    {
        var pending = new Queue<Type>();
        var visited = new HashSet<Type>();
        pending.Enqueue(typeof(LoadTestOptions));
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current))
                continue;
            yield return current;

            foreach (var property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var propertyType = UnwrapNullable(property.PropertyType);
                if (TryGetCollectionElementType(propertyType, out var elementType))
                    propertyType = UnwrapNullable(elementType);
                if (IsConfigurationObject(propertyType))
                    pending.Enqueue(propertyType);
            }
        }
    }

    /// <summary>
    /// 递归生成与 System.Text.Json camelCase 规则一致的完整字段路径，列表元素使用 <c>[]</c> 标识。
    /// </summary>
    /// <param name="configurationType">当前配置对象类型。</param>
    /// <param name="prefix">父对象 JSON 路径；根对象为空。</param>
    /// <returns>当前对象及所有嵌套对象的可序列化属性路径。</returns>
    private static IEnumerable<string> EnumerateConfigurationPaths(Type configurationType, string? prefix)
    {
        foreach (var property in configurationType
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.CanRead && property.CanWrite)
                     .OrderBy(property => property.MetadataToken))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var path = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
            yield return path;

            var propertyType = UnwrapNullable(property.PropertyType);
            if (TryGetCollectionElementType(propertyType, out var elementType))
            {
                elementType = UnwrapNullable(elementType);
                if (IsConfigurationObject(elementType))
                {
                    foreach (var nestedPath in EnumerateConfigurationPaths(elementType, $"{path}[]"))
                        yield return nestedPath;
                }
            }
            else if (IsConfigurationObject(propertyType))
            {
                foreach (var nestedPath in EnumerateConfigurationPaths(propertyType, path))
                    yield return nestedPath;
            }
        }
    }

    /// <summary>
    /// 判断类型是否属于 LoadTester 可递归展开的强类型配置对象。
    /// </summary>
    /// <param name="type">待检查类型。</param>
    /// <returns>类型位于配置命名空间且名称以 Options 结尾时返回 true。</returns>
    private static bool IsConfigurationObject(Type type) =>
        type.Namespace == typeof(LoadTestOptions).Namespace &&
        type.Name.EndsWith("Options", StringComparison.Ordinal);

    /// <summary>
    /// 尝试取得数组或泛型集合的元素类型。
    /// </summary>
    /// <param name="type">待检查的属性类型。</param>
    /// <param name="elementType">成功时返回集合元素类型。</param>
    /// <returns>类型是数组或单泛型参数集合时返回 true。</returns>
    private static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType &&
            type.GetGenericArguments() is [var candidate] &&
            typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            elementType = candidate;
            return true;
        }

        elementType = typeof(void);
        return false;
    }

    /// <summary>
    /// 移除可空值类型包装，使枚举和嵌套配置类型可以按真实类型参与检查。
    /// </summary>
    /// <param name="type">可能是 <see cref="Nullable{T}"/> 的类型。</param>
    /// <returns>可空类型的底层类型；普通类型原样返回。</returns>
    private static Type UnwrapNullable(Type type) => Nullable.GetUnderlyingType(type) ?? type;
}
